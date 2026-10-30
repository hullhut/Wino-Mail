using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinUIEx;
using WinUIEx.Messaging;
using WinRT;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Monitors window messages via WinUIEx's WindowMessageMonitor to ensure mouse wheel
/// events are reliably delivered to the scrollable element under the cursor (mail list,
/// reading pane WebView2, dialogs, etc.) when hovering.
/// </summary>
public sealed partial class WindowMouseWheelHandler : IDisposable
{
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private readonly WindowEx _window;
    private readonly WindowMessageMonitor _messageMonitor;
    private readonly IntPtr _hwnd;
    private bool _disposed;

    private WindowMouseWheelHandler(WindowEx window)
    {
        _window = window;
        _hwnd = window.GetWindowHandle();
        _messageMonitor = new WindowMessageMonitor(window);
        _messageMonitor.WindowMessageReceived += OnWindowMessageReceived;
        _window.Closed += OnWindowClosed;
    }

    public static WindowMouseWheelHandler Attach(WindowEx window)
    {
        return new WindowMouseWheelHandler(window);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closed -= OnWindowClosed;
        _messageMonitor.WindowMessageReceived -= OnWindowMessageReceived;
        _messageMonitor.Dispose();
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message.MessageId != WM_MOUSEWHEEL && e.Message.MessageId != WM_MOUSEHWHEEL)
        {
            return;
        }

        if (_window.Content is not FrameworkElement rootElement || rootElement.XamlRoot is null)
        {
            return;
        }

        // If Ctrl key is pressed, let default handle (e.g. zooming)
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            return;
        }

        bool isHorizontal = e.Message.MessageId == WM_MOUSEHWHEEL;
        short wheelDelta = unchecked((short)((long)e.Message.WParam >> 16));
        if (wheelDelta == 0) return;

        bool isShift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        short screenX = unchecked((short)((long)e.Message.LParam & 0xFFFF));
        short screenY = unchecked((short)((long)e.Message.LParam >> 16));

        POINT clientPt = new() { X = screenX, Y = screenY };
        ScreenToClient(_hwnd, ref clientPt);

        double scale = rootElement.XamlRoot.RasterizationScale;
        if (scale <= 0) scale = 1.0;

        Point dipPoint = new(clientPt.X / scale, clientPt.Y / scale);
        // Standard smooth scrolling increment (80 DIPs per notch)
        double scrollPixels = (wheelDelta / 120.0) * 80.0;

        // 1. Check open popups (ContentDialog, Flyout)
        var openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(rootElement.XamlRoot);
        if (openPopups is not null && openPopups.Count > 0)
        {
            for (int i = openPopups.Count - 1; i >= 0; i--)
            {
                var popup = openPopups[i];
                if (popup.Child is not null && popup.IsOpen)
                {
                    var popupHits = VisualTreeHelper.FindElementsInHostCoordinates(dipPoint, popup.Child);
                    if (TryScrollTarget(popupHits, isHorizontal, isShift, scrollPixels))
                    {
                        e.Handled = true;
                        e.Result = IntPtr.Zero;
                        return;
                    }
                }
            }
        }

        // 2. Check main window visual tree
        var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(dipPoint, rootElement);
        if (TryScrollTarget(hitElements, isHorizontal, isShift, scrollPixels))
        {
            e.Handled = true;
            e.Result = IntPtr.Zero;
        }
    }

    [DynamicWindowsRuntimeCast(typeof(WebView2))]
    [DynamicWindowsRuntimeCast(typeof(ScrollViewer))]
    private static bool TryScrollTarget(IEnumerable<UIElement>? elements, bool isHorizontal, bool isShift, double scrollPixels)
    {
        if (elements is null) return false;

        foreach (var element in elements)
        {
            if (element is Wino.Editor.WinoMailRenderer mailRenderer)
            {
                mailRenderer.ScrollBy(0, -scrollPixels);
                return true;
            }

            if (element is WebView2 webView)
            {
                if (webView.CoreWebView2 is not null)
                {
                    double deltaY = -scrollPixels;
                    _ = webView.ExecuteScriptAsync(
                        $"window.scrollBy({{ left: 0, top: {deltaY.ToString(CultureInfo.InvariantCulture)}, behavior: 'auto' }});");
                    return true;
                }
            }

            var scrollViewer = element as ScrollViewer ?? FindParent<ScrollViewer>(element);
            if (scrollViewer is not null)
            {
                if (!isHorizontal && !isShift)
                {
                    if (scrollViewer.ScrollableHeight > 0)
                    {
                        double current = scrollViewer.VerticalOffset;
                        double target = Math.Clamp(current - scrollPixels, 0, scrollViewer.ScrollableHeight);
                        if (Math.Abs(target - current) > 0.01)
                        {
                            scrollViewer.ChangeView(null, target, null, disableAnimation: false);
                            return true;
                        }
                    }
                }
                else
                {
                    if (scrollViewer.ScrollableWidth > 0)
                    {
                        double current = scrollViewer.HorizontalOffset;
                        double target = Math.Clamp(current - scrollPixels, 0, scrollViewer.ScrollableWidth);
                        if (Math.Abs(target - current) > 0.01)
                        {
                            scrollViewer.ChangeView(target, null, null, disableAnimation: false);
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is T typed) return typed;
        }
        return null;
    }
}
