using System.Globalization;
using IpCamera.Cameras;
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
internal sealed class CameraConfigFlow(FfmpegLocator ffmpeg, ILogger logger) : IConfigFlow
{
	private const string StepId = "camera";

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(ConfigFlowResult.Step(Step()));
	}

	public Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (stepId != StepId)
		{
			return Task.FromResult(ConfigFlowResult.Error(Step(), Strings.Setup.UnknownStep()));
		}

		var name = ReadString(input, "name")?.Trim() ?? string.Empty;
		if (name.Length == 0)
		{
			return Task.FromResult(FieldError("name", Strings.Setup.NameRequired()));
		}

		if (!CameraDefinition.TryReadKind(ReadString(input, "kind"), out var kind))
		{
			return Task.FromResult(FieldError("kind", Strings.Setup.KindRequired()));
		}

		var urlText = ReadString(input, "url")?.Trim() ?? string.Empty;
		if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
		{
			return Task.FromResult(FieldError("url", Strings.Setup.UrlInvalid()));
		}

		var schemeFits = kind == CameraSourceKind.Rtsp
			? url.Scheme is "rtsp" or "rtsps"
			: url.Scheme is "http" or "https";
		if (!schemeFits)
		{
			return Task.FromResult(FieldError("url", Strings.Setup.UrlWrongScheme()));
		}

		var fps = ReadNumber(input, CameraSettings.FpsKey, 5);
		if (fps is < 1 or > 15)
		{
			return Task.FromResult(FieldError(CameraSettings.FpsKey, Strings.Setup.FpsRange()));
		}

		var maxWidth = ReadNumber(input, CameraSettings.MaxWidthKey, 640);
		if (maxWidth is < 0 or > 3840)
		{
			return Task.FromResult(FieldError(CameraSettings.MaxWidthKey, Strings.Setup.WidthRange()));
		}

		if (kind == CameraSourceKind.Rtsp && !ffmpeg.IsAvailable)
		{
			logger.Warning("An RTSP camera was configured but ffmpeg is not on the PATH.");
			return Task.FromResult(ConfigFlowResult.Error(Step(), Strings.Setup.FfmpegMissing()));
		}

		var values = new Dictionary<string, ConfigFlowValue>
		{
			[CameraSettings.KindKey] = ConfigFlowValue.Plain(CameraDefinition.ToWireValue(kind)),
			[CameraSettings.UrlKey] = ConfigFlowValue.Plain(url.ToString()),
			[CameraSettings.UsernameKey] = ConfigFlowValue.Plain(ReadString(input, CameraSettings.UsernameKey)?.Trim() ?? string.Empty),
			[CameraSettings.FpsKey] = ConfigFlowValue.Plain(fps.ToString(CultureInfo.InvariantCulture)),
			[CameraSettings.MaxWidthKey] = ConfigFlowValue.Plain(maxWidth.ToString(CultureInfo.InvariantCulture)),
			[CameraSettings.RtspTransportKey] =
				ConfigFlowValue.Plain(ReadString(input, CameraSettings.RtspTransportKey) == "udp" ? "udp" : "tcp")
		};

		var password = ReadString(input, CameraSettings.PasswordKey);
		if (!string.IsNullOrEmpty(password) && password != "$masked")
		{
			values[CameraSettings.PasswordKey] = ConfigFlowValue.Secret(password);
		}

		return Task.FromResult(ConfigFlowResult.Complete(name, values));
	}

	private static ConfigFlowResult FieldError(string field, LocalizedText message)
	{
		return ConfigFlowResult.Error(
			Step(),
			message,
			new Dictionary<string, LocalizedText> { [field] = message });
	}

	private static ConfigFlowStep Step()
	{
		return new ConfigFlowStep
		{
			StepId = StepId,
			Title = Strings.Setup.Title(),
			Description = Strings.Setup.Description(),
			Fields =
			[
				ActionParameter.Text("name", label: Strings.Setup.Name(), required: true, maxLength: 100),
				ActionParameter.Choice(
					CameraSettings.KindKey,
					[
						new ActionParameterOption { Value = "snapshot", Label = Strings.Setup.KindSnapshot() },
						new ActionParameterOption { Value = "mjpeg", Label = Strings.Setup.KindMjpeg() },
						new ActionParameterOption { Value = "rtsp", Label = Strings.Setup.KindRtsp() }
					],
					label: Strings.Setup.Kind(),
					defaultValue: "snapshot",
					required: true),
				ActionParameter.Text(
					CameraSettings.UrlKey,
					label: Strings.Setup.Url(),
					description: Strings.Setup.UrlDescription(),
					placeholder: "http://192.168.1.20/snapshot.jpg",
					required: true)
			],
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
					.OnlyWhen(CameraSettings.KindKey, "rtsp")
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
