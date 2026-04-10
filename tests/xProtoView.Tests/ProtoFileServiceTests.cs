using xProtoView.Services;
using Xunit;

namespace xProtoView.Tests;

public sealed class ProtoFileServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"xProtoView.Tests.{Guid.NewGuid():N}");

    [Fact]
    public void CollectProtoFiles_ShouldRespectIncludeSubDirectoriesFlag()
    {
        Directory.CreateDirectory(_tempRoot);
        var rootProto = Path.Combine(_tempRoot, "root.proto");
        File.WriteAllText(rootProto, "syntax = \"proto3\";");

        var subDir = Path.Combine(_tempRoot, "nested");
        Directory.CreateDirectory(subDir);
        var subProto = Path.Combine(subDir, "sub.proto");
        File.WriteAllText(subProto, "syntax = \"proto3\";");

        var service = new ProtoFileService();
        var topOnly = service.CollectProtoFiles([
            new ProtoPathEntry
            {
                Path = _tempRoot,
                IsDirectory = true,
                IncludeSubDirectories = false
            }
        ]);
        Assert.Contains(Path.GetFullPath(rootProto), topOnly);
        Assert.DoesNotContain(Path.GetFullPath(subProto), topOnly);

        var recursive = service.CollectProtoFiles([
            new ProtoPathEntry
            {
                Path = _tempRoot,
                IsDirectory = true,
                IncludeSubDirectories = true
            }
        ]);
        Assert.Contains(Path.GetFullPath(rootProto), recursive);
        Assert.Contains(Path.GetFullPath(subProto), recursive);
    }

    [Fact]
    public void GetIncludeDirs_ShouldContainConfiguredDirectoryAndFileParent()
    {
        Directory.CreateDirectory(_tempRoot);
        var dir = Path.Combine(_tempRoot, "protos");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(_tempRoot, "single.proto");
        File.WriteAllText(file, "syntax = \"proto3\";");

        var service = new ProtoFileService();
        var includeDirs = service.GetIncludeDirs(
            [
                new ProtoPathEntry { Path = dir, IsDirectory = true, IncludeSubDirectories = false },
                new ProtoPathEntry { Path = file, IsDirectory = false, IncludeSubDirectories = false }
            ],
            [file]);

        Assert.Contains(Path.GetFullPath(dir), includeDirs);
        Assert.Contains(Path.GetFullPath(_tempRoot), includeDirs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
