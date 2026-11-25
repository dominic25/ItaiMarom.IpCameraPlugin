using IpCamera.Actions;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class SaveSnapshotTests
{
	[Test]
	public async Task A_snapshot_is_written_into_the_given_folder()
	{
		var folder = Path.Combine(Path.GetTempPath(), "ipcam-snapshot-" + Guid.NewGuid().ToString("N"));
		var frame = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };

		try
		{
			var path = await SnapshotWriter.SaveAsync(folder, "Front door", frame, CancellationToken.None);

			Assert.That(File.Exists(path), Is.True);
			Assert.That(Path.GetDirectoryName(path), Is.EqualTo(folder));
			Assert.That(Path.GetFileName(path), Does.StartWith("Front door-"));
			Assert.That(File.ReadAllBytes(path), Is.EqualTo(frame));
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	[Test]
	public void A_camera_name_becomes_a_safe_file_name()
	{
		var safe = SnapshotWriter.Sanitize("Front: door/1");

		Assert.That(safe, Does.Not.Contain(":"));
		Assert.That(safe, Does.Not.Contain("/"));
		Assert.That(safe, Does.Not.Contain("\\"));
	}

	[Test]
	public void An_empty_name_falls_back_to_camera()
	{
		Assert.That(SnapshotWriter.Sanitize("   "), Is.EqualTo("camera"));
	}
}
