using Chernika.Domain;

namespace Chernika.UnitTests;

public class NotificationRouteGuardTests
{
    [Theory]
    [InlineData("/хк/11111111-1111-1111-1111-111111111111")]
    [InlineData("/задачи/11111111-1111-1111-1111-111111111111")]
    [InlineData("/уведомления")]
    public void IsSafeTarget_ReturnsTrue_ForAllowedRoutes(string url)
    {
        Assert.True(NotificationRouteGuard.IsSafeTarget(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("http://evil.example.com/phish")]
    [InlineData("https://evil.example.com/phish")]
    [InlineData("//evil.example.com/phish")]
    [InlineData("/пользователи")]
    [InlineData("/поиск?q=x")]
    [InlineData("/задачи")]
    [InlineData("javascript:alert(1)")]
    public void IsSafeTarget_ReturnsFalse_ForNonWhitelistedRoutes(string? url)
    {
        Assert.False(NotificationRouteGuard.IsSafeTarget(url));
    }

    [Fact]
    public void Normalize_ReturnsUrl_WhenSafe()
    {
        var url = "/хк/11111111-1111-1111-1111-111111111111";
        Assert.Equal(url, NotificationRouteGuard.Normalize(url));
    }

    [Fact]
    public void Normalize_ReturnsNull_WhenUnsafe()
    {
        Assert.Null(NotificationRouteGuard.Normalize("http://evil.example.com"));
    }
}
