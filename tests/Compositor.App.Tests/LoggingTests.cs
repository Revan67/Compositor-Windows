using Compositor.App.Diagnostics;
using Compositor.App.ViewModels;

namespace Compositor.App.Tests;

public sealed class LoggingTests
{
    [Fact]
    public async Task LogCapturesCommandLifetimeAndFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"compositor-logs-{Guid.NewGuid():N}");
        try
        {
            AppLog.Initialize(directory);
            var observed = false;
            var command = new AsyncRelayCommand(() => throw new InvalidOperationException("diagnostic probe"), onError: _ => observed = true, name: "Test.Probe");
            await command.ExecuteAsync();
            var path = Assert.IsType<string>(AppLog.CurrentPath);
            AppLog.Shutdown();

            var contents = File.ReadAllText(path);
            Assert.True(observed);
            Assert.Contains("Begin Test.Probe", contents);
            Assert.Contains("Failed Test.Probe", contents);
            Assert.Contains("diagnostic probe", contents);
        }
        finally
        {
            AppLog.Shutdown();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
