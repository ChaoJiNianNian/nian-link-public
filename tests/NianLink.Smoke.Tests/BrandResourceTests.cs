using System.Windows;
using NianLink.Windows;

namespace NianLink.Smoke.Tests;

public sealed class BrandResourceTests
{
    [Fact]
    public void SettingsBrandImage_IsEmbeddedInApplicationAssembly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = typeof(StandaloneApplication).Assembly;
                var info = Application.GetResourceStream(new Uri(
                    "/NianLink;component/Assets/NianLinkBrand.png", UriKind.Relative));
                Assert.NotNull(info);
                using var stream = info.Stream;
                Span<byte> signature = stackalloc byte[8];
                stream.ReadExactly(signature);
                Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature.ToArray());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Brand resource check timed out.");
        if (failure is not null) throw new InvalidOperationException("Application brand resource could not be loaded.", failure);
    }
}
