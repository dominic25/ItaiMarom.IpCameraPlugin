using System.Globalization;
using IpCamera.Cameras;
using IpCamera.Onvif;
using IpCamera.Streaming;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace IpCamera.ConfigFlow;

/// <summary>
/// Adds or edits one camera. Each completed flow becomes one config entry, and every entry becomes one
/// live stream, which is why several cameras need no separate list screen.
/// </summary>
internal sealed class CameraConfigFlow(
	FfmpegLocator ffmpeg,
	ICameraProbe probe,
	IOnvifDiscovery onvifDiscovery,
	IOnvifClient onvifClient,
	ILogger logger) : IConfigFlow
{
	private const string StepId = "camera";
	private const string OnvifKey = "onvif";

	private readonly Dictionary<string, OnvifDevice> _onvifDevices = new(StringComparer.Ordinal);

	public async Task<ConfigFlowResult> StartAsync(
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Discovery runs on every setup session, including an edit, so a camera that appears later is
		// still offered. It costs a three second wait on a network with no ONVIF cameras.
		try
		{
			var devices = await onvifDiscovery
				.DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken)
				.ConfigureAwait(false);

			foreach (var device in devices)
			{
				_onvifDevices[device.XAddr] = device;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			logger.Debug("ONVIF discovery failed: {Reason}", exception.Message);
		}

		return ConfigFlowResult.Step(Step());
	}

	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (stepId != StepId)
		{
			return ConfigFlowResult.Error(Step(), Strings.Setup.UnknownStep());
		}

		var name = ReadString(input, "name")?.Trim() ?? string.Empty;
		if (name.Length == 0)
		{
			return FieldError("name", Strings.Setup.NameRequired());
		}

		var username = ReadString(input, CameraSettings.UsernameKey)?.Trim();
		var password = ReadString(input, CameraSettings.PasswordKey);
		if (password == "$masked")
		{
			// The host keeps the stored secret and never sends it to the flow during an edit.
			password = null;
		}

		var onvif = ReadString(input, OnvifKey);
		CameraSourceKind kind;
		Uri? url;

		if (!string.IsNullOrEmpty(onvif))
		{
			if (!_onvifDevices.TryGetValue(onvif, out var device))
			{
				return FieldError(OnvifKey, Strings.Setup.Onvif.Unknown());
			}

			var resolved = await onvifClient
				.ResolveAsync(device, username, password, cancellationToken)
				.ConfigureAwait(false);

			if (resolved is null)
			{
				return FieldError(OnvifKey, Strings.Setup.Onvif.ResolveFailed());
			}

			if (resolved.StreamUri is { } streamUri &&
				Uri.TryCreate(streamUri, UriKind.Absolute, out var rtspUrl) &&
				rtspUrl.Scheme is "rtsp" or "rtsps")
			{
				kind = CameraSourceKind.Rtsp;
				url = rtspUrl;
			}
			else if (resolved.SnapshotUri is { } snapshotUri &&
				Uri.TryCreate(snapshotUri, UriKind.Absolute, out var snapshotUrl) &&
				snapshotUrl.Scheme is "http" or "https")
			{
				kind = CameraSourceKind.Snapshot;
				url = snapshotUrl;
			}
			else
			{
				return FieldError(OnvifKey, Strings.Setup.Onvif.NoStream());
			}
		}
		else
		{
			if (!CameraDefinition.TryReadKind(ReadString(input, CameraSettings.KindKey), out kind))
			{
				return FieldError(CameraSettings.KindKey, Strings.Setup.KindRequired());
			}

			var urlText = ReadString(input, CameraSettings.UrlKey)?.Trim() ?? string.Empty;
			if (!Uri.TryCreate(urlText, UriKind.Absolute, out var manualUrl))
			{
				return FieldError(CameraSettings.UrlKey, Strings.Setup.UrlInvalid());
			}

			var schemeFits = kind == CameraSourceKind.Rtsp
				? manualUrl.Scheme is "rtsp" or "rtsps"
				: manualUrl.Scheme is "http" or "https";
			if (!schemeFits)
			{
				return FieldError(CameraSettings.UrlKey, Strings.Setup.UrlWrongScheme());
			}

			url = manualUrl;
		}

		if (url is null)
		{
			return FieldError(CameraSettings.UrlKey, Strings.Setup.UrlInvalid());
		}

		var fps = ReadNumber(input, CameraSettings.FpsKey, 5);
		if (fps is < 1 or > 15)
		{
			return FieldError(CameraSettings.FpsKey, Strings.Setup.FpsRange());
		}

		var maxWidth = ReadNumber(input, CameraSettings.MaxWidthKey, 640);
		if (maxWidth is < 0 or > 3840)
		{
			return FieldError(CameraSettings.MaxWidthKey, Strings.Setup.WidthRange());
		}

		var transport = ReadString(input, CameraSettings.RtspTransportKey) == "udp" ? "udp" : "tcp";

		var control = ReadString(input, CameraSettings.ControlKey) switch
		{
			"xiaomi" => "xiaomi",
			"none" => "none",
			_ => "onvif"
		};

		var controlHost = ReadString(input, CameraSettings.ControlHostKey)?.Trim();
		var controlToken = ReadString(input, CameraSettings.ControlTokenKey);
		if (controlToken == "$masked")
		{
			controlToken = null;
		}

		if (control == "xiaomi")
		{
			if (string.IsNullOrWhiteSpace(controlHost))
			{
				return FieldError(CameraSettings.ControlHostKey, Strings.Setup.ControlHostRequired());
			}

			if (!string.IsNullOrEmpty(controlToken) &&
				!IpCamera.Xiaomi.MiioClient.TryReadToken(controlToken, out _))
			{
				return FieldError(CameraSettings.ControlTokenKey, Strings.Setup.ControlTokenInvalid());
			}
		}

		if (kind == CameraSourceKind.Rtsp && !ffmpeg.IsAvailable)
		{
			logger.Warning("An RTSP camera was configured but ffmpeg is not on the PATH.");
			return ConfigFlowResult.Error(Step(), Strings.Setup.FfmpegMissing());
		}

		var candidate = new CameraDefinition(
			Guid.Empty.ToString("D"),
			name,
			kind,
			url,
			string.IsNullOrWhiteSpace(username) ? null : username,
			string.IsNullOrWhiteSpace(password) ? null : password,
			(int)Math.Round(fps),
			(int)Math.Round(maxWidth),
			transport);

		// An edit with an untouched secret cannot be probed: the flow sees the mask, not the password.
		if (string.IsNullOrEmpty(candidate.Username) || !string.IsNullOrEmpty(candidate.Password))
		{
			var probeResult = await probe.ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
			if (!probeResult.Succeeded)
			{
				return FieldError(CameraSettings.UrlKey, ProbeMessage(probeResult.Failure));
			}
		}

		var values = new Dictionary<string, ConfigFlowValue>
		{
			[CameraSettings.KindKey] = ConfigFlowValue.Plain(CameraDefinition.ToWireValue(kind)),
			[CameraSettings.UrlKey] = ConfigFlowValue.Plain(url.ToString()),
			[CameraSettings.UsernameKey] = ConfigFlowValue.Plain(username ?? string.Empty),
			[CameraSettings.FpsKey] = ConfigFlowValue.Plain(fps.ToString(CultureInfo.InvariantCulture)),
			[CameraSettings.MaxWidthKey] = ConfigFlowValue.Plain(maxWidth.ToString(CultureInfo.InvariantCulture)),
			[CameraSettings.RtspTransportKey] = ConfigFlowValue.Plain(transport),
			[CameraSettings.ControlKey] = ConfigFlowValue.Plain(control),
			[CameraSettings.ControlHostKey] = ConfigFlowValue.Plain(controlHost ?? string.Empty)
		};

		if (!string.IsNullOrEmpty(password))
		{
			values[CameraSettings.PasswordKey] = ConfigFlowValue.Secret(password);
		}

		if (!string.IsNullOrEmpty(controlToken))
		{
			values[CameraSettings.ControlTokenKey] = ConfigFlowValue.Secret(controlToken);
		}

		return ConfigFlowResult.Complete(name, values);
	}

	private static LocalizedText ProbeMessage(CameraProbeFailure failure)
	{
		return failure switch
		{
			CameraProbeFailure.Unauthorized => Strings.Setup.Probe.Unauthorized(),
			CameraProbeFailure.HttpError => Strings.Setup.Probe.HttpError(),
			CameraProbeFailure.NotJpeg => Strings.Setup.Probe.NotJpeg(),
			CameraProbeFailure.NoFrame => Strings.Setup.Probe.NoFrame(),
			_ => Strings.Setup.Probe.Unreachable()
		};
	}

	private ConfigFlowResult FieldError(string field, LocalizedText message)
	{
		return ConfigFlowResult.Error(
			Step(),
			message,
			new Dictionary<string, LocalizedText> { [field] = message });
	}

	private ConfigFlowStep Step()
	{
		var fields = new List<ActionParameter>
		{
			ActionParameter.Text("name", label: Strings.Setup.Name(), required: true, maxLength: 100)
		};

		if (_onvifDevices.Count > 0)
		{
			var options = new List<ActionParameterOption>
			{
				new() { Value = string.Empty, Label = Strings.Setup.Onvif.Manual() }
			};

			options.AddRange(_onvifDevices.Values
				.OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
				.Select(device => new ActionParameterOption
				{
					Value = device.XAddr,
					Label = LocalizedText.FromLiteral($"{device.DisplayName} ({device.Host})")
				}));

			fields.Add(ActionParameter.Choice(
				OnvifKey,
				options,
				label: Strings.Setup.Onvif.Label(),
				description: Strings.Setup.Onvif.Description()));
		}

		fields.Add(ActionParameter.Choice(
			CameraSettings.KindKey,
			[
				new ActionParameterOption { Value = "snapshot", Label = Strings.Setup.KindSnapshot() },
				new ActionParameterOption { Value = "mjpeg", Label = Strings.Setup.KindMjpeg() },
				new ActionParameterOption { Value = "rtsp", Label = Strings.Setup.KindRtsp() }
			],
			label: Strings.Setup.Kind(),
			defaultValue: "snapshot"));

		fields.Add(ActionParameter.Text(
			CameraSettings.UrlKey,
			label: Strings.Setup.Url(),
			description: Strings.Setup.UrlDescription(),
			placeholder: "http://192.168.1.20/snapshot.jpg"));

		fields.Add(ActionParameter.Choice(
			CameraSettings.QualityKey,
			[
				new ActionParameterOption { Value = "custom", Label = Strings.Setup.QualityCustom() },
				new ActionParameterOption { Value = "low", Label = Strings.Setup.QualityLow() },
				new ActionParameterOption { Value = "balanced", Label = Strings.Setup.QualityBalanced() },
				new ActionParameterOption { Value = "high", Label = Strings.Setup.QualityHigh() }
			],
			label: Strings.Setup.Quality(),
			description: Strings.Setup.QualityDescription(),
			defaultValue: "custom"));

		return new ConfigFlowStep
		{
			StepId = StepId,
			Title = Strings.Setup.Title(),
			Description = Strings.Setup.Description(),
			Fields = fields,
			AdvancedFields =
			[
				ActionParameter.Text(
					CameraSettings.UsernameKey,
					label: Strings.Setup.Username(),
					description: Strings.Setup.UsernameDescription()),
				ActionParameter.Secret(
					CameraSettings.PasswordKey,
					label: Strings.Setup.Password(),
					description: Strings.Setup.PasswordDescription()),
				ActionParameter.Number(
					CameraSettings.FpsKey,
					label: Strings.Setup.Fps(),
					description: Strings.Setup.FpsDescription(),
					min: 1,
					max: 15,
					step: 1,
					defaultValue: 5),
				ActionParameter.Number(
					CameraSettings.MaxWidthKey,
					label: Strings.Setup.MaxWidth(),
					description: Strings.Setup.MaxWidthDescription(),
					min: 0,
					max: 3840,
					step: 16,
					defaultValue: 640),
				ActionParameter.Choice(
					CameraSettings.RtspTransportKey,
					[
						new ActionParameterOption { Value = "tcp", Label = Strings.Setup.TransportTcp() },
						new ActionParameterOption { Value = "udp", Label = Strings.Setup.TransportUdp() }
					],
					label: Strings.Setup.RtspTransport(),
					defaultValue: "tcp")
					.OnlyWhen(CameraSettings.KindKey, "rtsp"),
				ActionParameter.Choice(
					CameraSettings.ControlKey,
					[
						new ActionParameterOption { Value = "onvif", Label = Strings.Setup.ControlOnvif() },
						new ActionParameterOption { Value = "xiaomi", Label = Strings.Setup.ControlXiaomi() },
						new ActionParameterOption { Value = "none", Label = Strings.Setup.ControlNone() }
					],
					label: Strings.Setup.Control(),
					description: Strings.Setup.ControlDescription(),
					defaultValue: "onvif"),
				ActionParameter.Text(
					CameraSettings.ControlHostKey,
					label: Strings.Setup.ControlHost(),
					description: Strings.Setup.ControlHostDescription(),
					placeholder: "192.168.1.30"),
				ActionParameter.Secret(
					CameraSettings.ControlTokenKey,
					label: Strings.Setup.ControlToken(),
					description: Strings.Setup.ControlTokenDescription())
					.OnlyWhen(CameraSettings.ControlKey, "xiaomi")
			]
		};
	}

	private static string? ReadString(IReadOnlyDictionary<string, object?> input, string key)
	{
		return input.TryGetValue(key, out var value) ? value as string : null;
	}

	private static double ReadNumber(IReadOnlyDictionary<string, object?> input, string key, double fallback)
	{
		if (!input.TryGetValue(key, out var value) || value is null)
		{
			return fallback;
		}

		return value switch
		{
			double number => number,
			int number => number,
			long number => number,
			string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
			_ => fallback
		};
	}
}
