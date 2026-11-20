using ProcessTracker.Core.Sessions;

namespace ProcessTracker.Core.Tests.Sessions;

public sealed class TranscriptReaderTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "pt-tr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_profile)) Directory.Delete(_profile, true);
    }

    private string Write(string id, params string[] lines)
    {
        var dir = Path.Combine(_profile, ".claude", "projects", "C--repos-X");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Read_TakesFirstCwdAndFirstUserText()
    {
        var path = Write("abc",
            """{"type":"bridge-session","sessionId":"abc"}""",
            """{"type":"attachment","cwd":"C:\\repos\\X","sessionId":"abc"}""",
            """{"type":"user","message":{"role":"user","content":"In the past couple\n of   weeks…"},"cwd":"C:\\repos\\Y","sessionId":"abc"}""");
        var info = new TranscriptReader(_profile).Read("abc");
        Assert.NotNull(info);
        Assert.Equal(@"C:\repos\X", info.Cwd);
        Assert.Equal("In the past couple of weeks…", info.Title);
        Assert.Equal(File.GetLastWriteTimeUtc(path), info.LastWriteUtc);
    }

    [Fact]
    public void Read_ArrayContent_UsesFirstTextItem()
    {
        Write("arr", """{"type":"user","message":{"content":[{"type":"text","text":"hello"}]},"cwd":"C:\\a"}""");
        Assert.Equal("hello", new TranscriptReader(_profile).Read("arr")!.Title);
    }

    [Fact]
    public void Read_MissingFile_Null()
    {
        Assert.Null(new TranscriptReader(_profile).Read("nope"));
        Write("other", "{}");
        Assert.Null(new TranscriptReader(_profile).Read("nope"));
    }

    [Fact]
    public void Read_TitleTruncatedTo80()
    {
        var text = new string('x', 200);
        Write("long", "{\"type\":\"user\",\"message\":{\"content\":\"" + text + "\"},\"cwd\":\"C:\\\\a\"}");
        Assert.Equal(80, new TranscriptReader(_profile).Read("long")!.Title!.Length);
    }

    [Fact]
    public void Read_HugeFirstLine_BoundedAndNoException()
    {
        var huge = "{\"type\":\"attachment\",\"blob\":\"" + new string('A', 400 * 1024) + "\"}";
        Write("huge1", huge, "{\"type\":\"user\",\"message\":{\"content\":\"late\"},\"cwd\":\"C:/a\"}");
        var info = new TranscriptReader(_profile).Read("huge1");
        Assert.NotNull(info);
        Assert.Null(info.Cwd);
        Assert.Null(info.Title);
    }

    [Fact]
    public void Read_HugeLineAfterCwd_KeepsCwd()
    {
        var huge = "{\"type\":\"attachment\",\"blob\":\"" + new string('A', 400 * 1024) + "\"}";
        Write("huge2", "{\"type\":\"attachment\",\"cwd\":\"C:/a\"}", huge);
        var info = new TranscriptReader(_profile).Read("huge2");
        Assert.Equal("C:/a", info!.Cwd);
        Assert.Null(info.Title);
    }

    [Fact]
    public void Read_EmptyId_Null() => Assert.Null(new TranscriptReader(_profile).Read(""));

    [Fact]
    public void Read_TitleTruncation_DoesNotSplitSurrogatePair()
    {
        var text = new string('x', 79) + "😀tail";
        Write("sur", "{\"type\":\"user\",\"message\":{\"content\":\"" + text + "\"},\"cwd\":\"C:/a\"}");
        var title = new TranscriptReader(_profile).Read("sur")!.Title!;
        Assert.Equal(79, title.Length);
    }
}
