using ScreenReceiver.Contracts;
using ScreenReceiver.Server.Services;
using Xunit;

namespace ScreenReceiver.UnitTests;

public class FrameStoreTests
{
    [Fact]
    public void GetLatest_BeforeUpdate_ReturnsNull()
    {
        var store = new FrameStore();
        Assert.Null(store.GetLatest());
    }

    [Fact]
    public void Update_ThenGetLatest_ReturnsData()
    {
        var store = new FrameStore();
        var bytes = new byte[] { 1, 2, 3 };
        var meta = new FrameMetadata(1, DateTimeOffset.UtcNow, 1920, 1080, "image/jpeg", 3);

        store.Update(bytes, meta);
        var latest = store.GetLatest();

        Assert.NotNull(latest);
        Assert.Equal(3, latest!.Value.bytes.Length);
        Assert.Equal(1, latest.Value.meta.FrameId);
        Assert.Equal("image/jpeg", latest.Value.meta.ContentType);
    }

    [Fact]
    public void Update_SecondFrame_ReplacesPrevious()
    {
        var store = new FrameStore();
        store.Update(new byte[] { 1 }, new FrameMetadata(1, DateTimeOffset.UtcNow, 1, 1, "image/jpeg", 1));
        store.Update(new byte[] { 2, 2 }, new FrameMetadata(2, DateTimeOffset.UtcNow, 1, 1, "image/jpeg", 2));

        var latest = store.GetLatest();

        Assert.NotNull(latest);
        Assert.Equal(2, latest!.Value.meta.FrameId);
        Assert.Equal(2, latest.Value.bytes.Length);
    }

    [Fact]
    public void Clear_AfterUpdate_ReturnsNull()
    {
        var store = new FrameStore();
        store.Update(new byte[] { 1 }, new FrameMetadata(1, DateTimeOffset.UtcNow, 1, 1, "image/jpeg", 1));
        store.Clear();
        Assert.Null(store.GetLatest());
    }
}

public class SessionServiceTests
{
    [Fact]
    public void Token_IsNonEmptyUrlSafe()
    {
        var session = new SessionService();
        Assert.False(string.IsNullOrWhiteSpace(session.Token));
        Assert.DoesNotContain('+', session.Token);
        Assert.DoesNotContain('/', session.Token);
    }

    [Fact]
    public void ValidateToken_CorrectBearerHeader_ReturnsTrue()
    {
        var session = new SessionService();
        Assert.True(session.ValidateToken($"Bearer {session.Token}"));
    }

    [Fact]
    public void ValidateToken_WrongToken_ReturnsFalse()
    {
        var session = new SessionService();
        Assert.False(session.ValidateToken("Bearer wrong-token"));
        Assert.False(session.ValidateToken(null));
        Assert.False(session.ValidateToken(""));
    }

    [Fact]
    public void StartStop_FlipsSharingState()
    {
        var session = new SessionService();
        Assert.False(session.IsSharing);
        session.StartSharing();
        Assert.True(session.IsSharing);
        session.StopSharing();
        Assert.False(session.IsSharing);
    }
}
