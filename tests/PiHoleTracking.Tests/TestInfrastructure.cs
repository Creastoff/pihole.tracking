using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace PiHoleTracking.Tests;

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "PiHoleTracking.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TemporaryDataDirectory : IDisposable
{
    private const string DataDirectoryVariable = "PIHOLE_REVIEW_DATA_DIR";
    private readonly string? _previousValue;

    public TemporaryDataDirectory()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "pihole-domain-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        _previousValue = System.Environment.GetEnvironmentVariable(DataDirectoryVariable);
        System.Environment.SetEnvironmentVariable(DataDirectoryVariable, RootPath);
    }

    public string RootPath { get; }

    public TestHostEnvironment Environment => new() { ContentRootPath = RootPath };

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(DataDirectoryVariable, _previousValue);
        if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
    }
}
