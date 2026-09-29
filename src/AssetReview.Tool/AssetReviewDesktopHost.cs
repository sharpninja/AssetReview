using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SharpNinja.AssetReview.Tool;

internal sealed record AssetReviewHostContext(ReviewOptions Options, Uri ReviewUri);

internal static class AssetReviewDesktopHost
{
    public static AssetReviewHostContext? Context { get; set; }
}

internal sealed class AssetReviewApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var context = AssetReviewDesktopHost.Context
                ?? throw new InvalidOperationException("Asset review host context was not initialized.");
            desktop.MainWindow = new AssetReviewWindow(context);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class AssetReviewWindow : Window
{
    private readonly AssetReviewHostContext _context;
    private readonly NativeWebView _webView = new();
    private string? _lastTheme;

    public AssetReviewWindow(AssetReviewHostContext context)
    {
        _context = context;
        Title = context.Options.Title;
        Width = 1360;
        Height = 900;
        MinWidth = 1040;
        MinHeight = 720;
        Content = _webView;

        Opened += (_, _) =>
        {
            _webView.Source = BuildThemeUri(CurrentThemeName());
            ApplyThemeToWebView();
        };
        ActualThemeVariantChanged += (_, _) => ApplyThemeToWebView();
        _webView.NavigationCompleted += (_, _) => ApplyThemeToWebView();
    }

    private string CurrentThemeName()
        => ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";

    private Uri BuildThemeUri(string theme)
    {
        var builder = new UriBuilder(_context.ReviewUri);
        var separator = string.IsNullOrWhiteSpace(builder.Query) ? string.Empty : builder.Query.TrimStart('&', '?') + "&";
        builder.Query = $"{separator}theme={theme}";
        return builder.Uri;
    }

    private async void ApplyThemeToWebView()
    {
        var theme = CurrentThemeName();
        if (_lastTheme == theme)
        {
            return;
        }

        _lastTheme = theme;
        try
        {
            await _webView.InvokeScript($"window.assetReviewSetTheme && window.assetReviewSetTheme('{theme}')");
        }
        catch
        {
            // Navigation may still be initializing. The URL query and page media query cover startup.
        }
    }
}
