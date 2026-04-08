using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Image = System.Windows.Controls.Image;
using TomsToolbox.Essentials;
using TomsToolbox.Wpf;
using static RegionToShare.NativeMethods;

namespace RegionToShare;

/// <summary>
/// Interaction logic for RecordingWindow.xaml
/// </summary>
public partial class RecordingWindow
{
    public static readonly Thickness BorderSize = new(4);

    private readonly HighResolutionTimer _timer;
    private readonly MainWindow _mainWindow;
    private readonly Image _renderTarget;
    private readonly bool _drawShadowCursor;
    private readonly POINT _debugOffset;
    private HwndTarget? _compositionTarget;

    private RECT _nativeMainWindowRect;
    private int _timerMutex;
    private IntPtr _windowHandle;
    // DXGI capture removed; using native BitBlt capture now

    public RecordingWindow(Image renderTarget, bool drawShadowCursor, int framesPerSecond, POINT debugOffset)
    {
        InitializeComponent();

        _mainWindow = (MainWindow)GetWindow(renderTarget)!;
        _renderTarget = renderTarget;
        _drawShadowCursor = drawShadowCursor;
        _debugOffset = debugOffset;

        _timer = new HighResolutionTimer(Timer_Tick, TimeSpan.FromSeconds(1.0 / framesPerSecond));
        _timer.Start();
    }

    public void UpdateSizeAndPos(RECT mainWindowRect)
    {
        _nativeMainWindowRect = mainWindowRect;
        NativeWindowRect = _nativeMainWindowRect + NativeBorderSize;
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        var hwndSource = (HwndSource?)PresentationSource.FromDependencyObject(this);
        if (hwndSource == null)
            return;

        hwndSource.AddHook(WindowProc);

        _compositionTarget = hwndSource.CompositionTarget;
        _windowHandle = hwndSource.Handle;

        var rect = _mainWindow.NativeWindowRect + NativeBorderSize;

        NativeWindowRect = rect;

        this.BeginInvoke(OnSizeOrPositionChanged);

        base.OnSourceInitialized(e);
    }

    private Transformations DeviceTransformations => _compositionTarget.GetDeviceTransformations();

    private RECT NativeWindowRect
    {
        get
        {
            GetWindowRect(_windowHandle, out var rect);
            return rect;
        }
        set
        {
            if (_windowHandle == IntPtr.Zero)
                return;

            SetWindowPos(_windowHandle, IntPtr.Zero, value.Left, value.Top, value.Width, value.Height, SWP_NOACTIVATE | SWP_NOZORDER);
        }
    }

    private Thickness NativeBorderSize => DeviceTransformations.ToDevice.Transform(BorderSize);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == WindowStateProperty)
        {
            this.BeginInvoke(DispatcherPriority.Background, () =>
            {
                WindowState = WindowState.Normal;
                OnSizeOrPositionChanged();
            });

            return;
        }

        if (e.Property != LeftProperty
            && e.Property != TopProperty
            && e.Property != ActualWidthProperty
            && e.Property != ActualHeightProperty)
            return;

        this.BeginInvoke(DispatcherPriority.Background, OnSizeOrPositionChanged);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        _timer.Stop();
    }

    public void PauseCapture()
    {
        _timer?.Stop();
    }

    public void ResumeCapture()
    {
        _timer?.Start();
    }

    private void OnSizeOrPositionChanged()
    {
        if (!IsLoaded)
            return;

        _mainWindow.NativeWindowRect = _nativeMainWindowRect = NativeWindowRect - NativeBorderSize;
    }

    private IntPtr WindowProc(IntPtr windowHandle, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return (IntPtr)NcHitTest(windowHandle, lParam);
        }

        return IntPtr.Zero;
    }

    private HitTest NcHitTest(IntPtr windowHandle, IntPtr lParam)
    {
        if (WindowState.Normal != WindowState)
            return HitTest.Client;

        if ((ResizeMode != ResizeMode.CanResize) && ResizeMode != ResizeMode.CanResizeWithGrip)
            return HitTest.Client;

        // Arguments are absolute native coordinates
        var hitPoint = new POINT((short)lParam, (short)((uint)lParam >> 16));

        GetWindowRect(windowHandle, out var windowRect);

        var topLeft = windowRect.TopLeft;
        var bottomRight = windowRect.BottomRight;

        var transformations = DeviceTransformations;

        var borderSize = transformations.ToDevice.Transform(BorderSize);

        var clientPoint = transformations.FromDevice.Transform(hitPoint - topLeft);

        if (InputHitTest(clientPoint) is FrameworkElement element)
        {
            if (element.AncestorsAndSelf().OfType<ButtonBase>().Any())
            {
                return HitTest.Client;
            }
        }

        var left = topLeft.X;
        var top = topLeft.Y;
        var right = bottomRight.X;
        var bottom = bottomRight.Y;

        if ((hitPoint.Y < top) || (hitPoint.Y > bottom) || (hitPoint.X < left) || (hitPoint.X > right))
            return HitTest.Transparent;

        if ((hitPoint.Y < (top + borderSize.Top)) && (hitPoint.X < (left + borderSize.Left)))
            return HitTest.TopLeft;
        if ((hitPoint.Y < (top + borderSize.Top)) && (hitPoint.X > (right - borderSize.Right)))
            return HitTest.TopRight;
        if ((hitPoint.Y > (bottom - borderSize.Bottom)) && (hitPoint.X < (left + borderSize.Left)))
            return HitTest.BottomLeft;
        if ((hitPoint.Y > (bottom - borderSize.Bottom)) && (hitPoint.X > (right - borderSize.Right)))
            return HitTest.BottomRight;
        if (hitPoint.Y < (top + borderSize.Top))
            return HitTest.Caption;
        if (hitPoint.Y > (bottom - borderSize.Bottom))
            return HitTest.Bottom;
        if (hitPoint.X < (left + borderSize.Left))
            return HitTest.Left;
        if (hitPoint.X > (right - borderSize.Right))
            return HitTest.Right;

        return HitTest.Client;
    }

    private void Timer_Tick(TimeSpan elapsed)
    {
        if (Interlocked.CompareExchange(ref _timerMutex, 1, 0) != 0)
            return;

        try
        {
            // Explicitly call the WPF Dispatcher extension to avoid ambiguity with TomsToolbox extension methods
            System.Windows.Threading.DispatcherExtensions.BeginInvoke(Dispatcher, Timer_Tick);
        }
        catch
        {
            // Window already unloaded
        }
    }

    private void Timer_Tick()
    {
            try
            {
                var nativeRect = _nativeMainWindowRect - _debugOffset;

                var screenHdc = GetDC(IntPtr.Zero);
                var memHdc = CreateCompatibleDC(screenHdc);
                IntPtr hBitmap = IntPtr.Zero;
                IntPtr oldBitmap = IntPtr.Zero;
                try
                {
                    hBitmap = CreateCompatibleBitmap(screenHdc, nativeRect.Width, nativeRect.Height);
                    oldBitmap = SelectObject(memHdc, hBitmap);

                    // BitBlt from screen DC to memory DC
                    BitBlt(memHdc, 0, 0, nativeRect.Width, nativeRect.Height, screenHdc, nativeRect.Left, nativeRect.Top, SRCCOPY);

                    if (_drawShadowCursor)
                    {
                        // Draw cursor directly into the memory DC
                        ExtensionMethods.DrawCursor(memHdc, nativeRect);
                    }

                    var imageSource = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                    _renderTarget.Source = imageSource;
                }
                finally
                {
                    if (oldBitmap != IntPtr.Zero)
                        SelectObject(memHdc, oldBitmap);
                    if (hBitmap != IntPtr.Zero)
                        DeleteObject(hBitmap);
                    if (memHdc != IntPtr.Zero)
                        DeleteDC(memHdc);
                    if (screenHdc != IntPtr.Zero)
                        ReleaseDC(IntPtr.Zero, screenHdc);
                }
            }
        catch
        {
            // Window already unloaded
        }
        finally
        {
            Interlocked.Exchange(ref _timerMutex, 0);
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}