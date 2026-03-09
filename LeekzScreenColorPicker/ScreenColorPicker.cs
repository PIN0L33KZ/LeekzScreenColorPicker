using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LeekzScreenColorPicker;

[ToolboxItem(true)]
[DefaultProperty(nameof(PickedColor))]
[DefaultEvent(nameof(ColorPicked))]
public class ScreenColorPickerControl : UserControl
{
    private const int DefaultPreviewCellsPerAxis = 15;
    private const int AdaptiveGridSampleTarget = 12;

    private readonly System.Windows.Forms.Timer _updateTimer;

    private Bitmap? _sampleBitmap;
    private Bitmap? _pickedBitmap;
    private bool _ignoreCaptureChanged;

    private Point _lastCursorScreenPoint;
    private Color _pickedColor = Color.Empty;

#if NET10_0_OR_GREATER
    private ScreenCaptureMode? _previousScreenCaptureMode;
#endif

    public ScreenColorPickerControl()
    {
        // These styles keep the preview responsive and reduce flicker.
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        TabStop = true;
        Cursor = Cursors.Cross;

        BackColor = Color.Transparent;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(8);
        Text = "Click and hold";

        CurrentColor = GetIdleSurfaceColor();

        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = 20
        };
        _updateTimer.Tick += OnUpdateTimerTick;
    }

    [Category("Appearance")]
    [Description("Gets or sets the placeholder text shown when no live or frozen preview is available.")]
    [DefaultValue("Click and hold")]
    [Browsable(true)]
    [EditorBrowsable(EditorBrowsableState.Always)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    [Localizable(true)]
    public override string Text
    {
        get => base.Text;
        set
        {
            string normalized = value ?? string.Empty;

            if(base.Text == normalized)
            {
                return;
            }

            base.Text = normalized;
            Invalidate();
        }
    }

    /// <summary>
    /// Gets a value indicating whether a live pick operation is in progress.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPicking { get; private set; }

    /// <summary>
    /// Gets a value indicating whether a committed colour is currently stored.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HasPickedColor { get; private set; }

    /// <summary>
    /// Gets the colour currently shown by the control.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CurrentColor { get; private set; }

    [Category("Appearance")]
    [Description("Gets or sets the committed colour. Assigning this value updates the stored selection, but only a completed pick creates a frozen preview.")]
    [DefaultValue(typeof(Color), "Empty")]
    public Color PickedColor
    {
        get => _pickedColor;
        set => SetPickedColorInternal(
            value,
            raiseEvent: true,
            updateCurrentColorWhenIdle: true,
            clearFrozenPreview: true);
    }

    [Category("Appearance")]
    [Description("Gets or sets the approximate size of each sampled pixel cell.")]
    [DefaultValue(12)]
    public int Zoom
    {
        get;
        set
        {
            int normalized = Math.Clamp(value, 4, 40);

            if(field == normalized)
            {
                return;
            }

            field = normalized;
            UpdateLayoutFromProperties();
            ClearFrozenPreviewIfIdle();
        }
    } = 12;

    [Category("Appearance")]
    [Description("Gets or sets whether grid lines are drawn over the preview.")]
    [DefaultValue(true)]
    public bool ShowGrid
    {
        get;
        set
        {
            if(field == value)
            {
                return;
            }

            field = value;
            Invalidate();
        }
    } = true;

    [Category("Appearance")]
    [Description("Gets or sets whether the grid colour adapts to the brightness of the visible content.")]
    [DefaultValue(true)]
    public bool UseAdaptiveGridColor
    {
        get;
        set
        {
            if(field == value)
            {
                return;
            }

            field = value;
            Invalidate();
        }
    } = true;

    [Category("Appearance")]
    [Description("Gets or sets the grid colour used over dark content. This is also used as the idle surface colour when no opaque background is available.")]
    [DefaultValue(typeof(Color), "White")]
    public Color DarkGridColor
    {
        get;
        set
        {
            if(field.ToArgb() == value.ToArgb())
            {
                return;
            }

            field = value;
            UpdateIdleCurrentColorIfNeeded();
            Invalidate();
        }
    } = Color.White;

    [Category("Appearance")]
    [Description("Gets or sets the grid colour used over light content when adaptive grid colouring is enabled.")]
    [DefaultValue(typeof(Color), "Black")]
    public Color LightGridColor
    {
        get;
        set
        {
            if(field.ToArgb() == value.ToArgb())
            {
                return;
            }

            field = value;
            Invalidate();
        }
    } = Color.Black;

    [Category("Property Changed")]
    [Description("Occurs when the live colour under the centre pixel changes during an active pick operation.")]
    public event EventHandler? CurrentColorChanged;

    [Category("Property Changed")]
    [Description("Occurs when the committed colour changes.")]
    public event EventHandler? PickedColorChanged;

    [Category("Action")]
    [Description("Occurs when a pick operation is completed and the final colour is committed.")]
    public event EventHandler<ScreenColorPickedEventArgs>? ColorPicked;

    public override Size GetPreferredSize(Size proposedSize)
    {
        int scaledZoom = Math.Max(1, ScaleLogical(Zoom));

        int previewWidth = DefaultPreviewCellsPerAxis * scaledZoom;
        int previewHeight = DefaultPreviewCellsPerAxis * scaledZoom;

        return new Size(
            Padding.Horizontal + previewWidth,
            Padding.Vertical + previewHeight);
    }

    /// <summary>
    /// Starts live screen sampling.
    /// </summary>
    public void StartPicking()
    {
        if(IsPicking || !Enabled || IsInDesigner())
        {
            return;
        }

        Focus();

        IsPicking = true;
        _lastCursorScreenPoint = Cursor.Position;

#if NET10_0_OR_GREATER
        ApplyFormScreenCaptureMode();
#endif

        _ignoreCaptureChanged = true;
        Capture = true;
        _ignoreCaptureChanged = false;

        UpdateSample();
        _updateTimer.Start();
        Invalidate();
    }

    /// <summary>
    /// Stops the current pick operation without committing the selection.
    /// </summary>
    public void CancelPicking()
    {
        EndPicking(commitSelection: false);
    }

    /// <summary>
    /// Clears the committed colour and any frozen preview.
    /// </summary>
    public void ClearPickedColor()
    {
        SetPickedColorInternal(
            Color.Empty,
            raiseEvent: true,
            updateCurrentColorWhenIdle: true,
            clearFrozenPreview: true);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        UpdateIdleCurrentColorIfNeeded();
        Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        UpdateIdleCurrentColorIfNeeded();
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateLayoutFromProperties();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        UpdateLayoutFromProperties();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        if(IsPicking)
        {
            UpdateSample();
        }
        else
        {
            ClearFrozenPreviewIfIdle();
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if(e.Button == MouseButtons.Left &&
            Enabled &&
            !IsInDesigner() &&
            GetPreviewBounds().Contains(e.Location))
        {
            StartPicking();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if(IsPicking)
        {
            UpdateSample();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if(IsPicking && e.Button == MouseButtons.Left)
        {
            EndPicking(commitSelection: true);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        if(IsPicking && !_ignoreCaptureChanged)
        {
            EndPicking(commitSelection: false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && !IsPicking)
        {
            StartPicking();
            e.Handled = true;
            return;
        }

        if(e.KeyCode == Keys.Escape && IsPicking)
        {
            CancelPicking();
            e.Handled = true;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if(BackColor.A == 255)
        {
            using SolidBrush brush = new(BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
            return;
        }

        if(Parent is not null)
        {
            PaintTransparentBackground(e.Graphics);

            if(BackColor.A > 0)
            {
                using SolidBrush alphaBrush = new(BackColor);
                e.Graphics.FillRectangle(alphaBrush, ClientRectangle);
            }

            return;
        }

        using SolidBrush fallbackBrush = new(SystemColors.Control);
        e.Graphics.FillRectangle(fallbackBrush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        Rectangle previewBounds = GetPreviewBounds();
        if(previewBounds.Width <= 1 || previewBounds.Height <= 1)
        {
            return;
        }

        int borderThickness = Math.Max(1, ScaleLogical(1));
        Rectangle contentBounds = Rectangle.Inflate(previewBounds, -borderThickness, -borderThickness);
        if(contentBounds.Width <= 0 || contentBounds.Height <= 0)
        {
            return;
        }

        using(Pen borderPen = new(ForeColor, borderThickness))
        {
            g.DrawRectangle(
                borderPen,
                previewBounds.X,
                previewBounds.Y,
                previewBounds.Width - 1,
                previewBounds.Height - 1);
        }

        Bitmap? visibleBitmap = GetVisibleBitmap();
        if(visibleBitmap is not null)
        {
            g.DrawImage(visibleBitmap, contentBounds);
        }
        else
        {
            using SolidBrush idleBrush = new(GetIdleSurfaceColor());
            g.FillRectangle(idleBrush, contentBounds);
        }

        (int columns, int rows) = visibleBitmap is not null
            ? (visibleBitmap.Width, visibleBitmap.Height)
            : GetDynamicSampleGridSize();

        Color gridColor = GetCurrentGridColor();

        if(ShowGrid)
        {
            DrawPixelGrid(g, contentBounds, gridColor, columns, rows);
        }

        if(IsPicking && _sampleBitmap is not null)
        {
            DrawCenterMarker(g, contentBounds, gridColor, _sampleBitmap.Width, _sampleBitmap.Height);
        }
        else if(_pickedBitmap is null)
        {
            DrawHelpOverlayText(g, contentBounds);
        }

        if(Focused && ShowFocusCues)
        {
            Rectangle focusRect = ClientRectangle;
            focusRect.Inflate(-ScaleLogical(2), -ScaleLogical(2));
            ControlPaint.DrawFocusRectangle(g, focusRect);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            _updateTimer.Tick -= OnUpdateTimerTick;
            _updateTimer.Dispose();
            _sampleBitmap?.Dispose();
            _pickedBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected virtual void OnCurrentColorChanged(EventArgs e)
    {
        CurrentColorChanged?.Invoke(this, e);
    }

    protected virtual void OnPickedColorChanged(EventArgs e)
    {
        PickedColorChanged?.Invoke(this, e);
    }

    protected virtual void OnColorPicked(ScreenColorPickedEventArgs e)
    {
        ColorPicked?.Invoke(this, e);
    }

    private void OnUpdateTimerTick(object? sender, EventArgs e)
    {
        UpdateSample();
    }

    private void UpdateLayoutFromProperties()
    {
        if(AutoSize)
        {
            Size = GetPreferredSize(Size.Empty);
        }

        Invalidate();
    }

    private void UpdateSample()
    {
        if(!IsPicking || IsDisposed)
        {
            return;
        }

        _lastCursorScreenPoint = Cursor.Position;

        (int columns, int rows) = GetDynamicSampleGridSize();

        int halfColumns = columns / 2;
        int halfRows = rows / 2;

        // The sampled area stays odd-sized so there is always a true centre pixel.
        Rectangle sourceRect = new(
            _lastCursorScreenPoint.X - halfColumns,
            _lastCursorScreenPoint.Y - halfRows,
            columns,
            rows);

        Bitmap sampleBitmap = EnsureSampleBitmap(columns, rows);

        using(Graphics g = Graphics.FromImage(sampleBitmap))
        {
            g.Clear(Color.Black);

            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            Rectangle actualRect = Rectangle.Intersect(sourceRect, virtualScreen);

            if(!actualRect.IsEmpty)
            {
                Point destination = new(
                    actualRect.X - sourceRect.X,
                    actualRect.Y - sourceRect.Y);

                g.CopyFromScreen(
                    actualRect.Location,
                    destination,
                    actualRect.Size,
                    CopyPixelOperation.SourceCopy);
            }
        }

        SetCurrentColor(GetCenterPixelColor(sampleBitmap));
        Invalidate();
    }

    private Bitmap EnsureSampleBitmap(int width, int height)
    {
        if(_sampleBitmap is not null &&
            _sampleBitmap.Width == width &&
            _sampleBitmap.Height == height)
        {
            return _sampleBitmap;
        }

        _sampleBitmap?.Dispose();
        _sampleBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        return _sampleBitmap;
    }

    private void EndPicking(bool commitSelection)
    {
        if(!IsPicking)
        {
            return;
        }

        if(commitSelection)
        {
            UpdateSample();
        }

        IsPicking = false;
        _updateTimer.Stop();

        _ignoreCaptureChanged = true;
        Capture = false;
        _ignoreCaptureChanged = false;

#if NET10_0_OR_GREATER
        RestoreFormScreenCaptureMode();
#endif

        if(commitSelection && _sampleBitmap is not null)
        {
            SetFrozenPreview((Bitmap)_sampleBitmap.Clone());

            SetPickedColorInternal(
                CurrentColor,
                raiseEvent: true,
                updateCurrentColorWhenIdle: true,
                clearFrozenPreview: false);

            OnColorPicked(new ScreenColorPickedEventArgs(
                PickedColor,
                CurrentColor,
                _lastCursorScreenPoint,
                FormatHex(PickedColor)));
        }
        else
        {
            SetCurrentColor(GetRestingDisplayColor());
        }

        Invalidate();
    }

    private void SetPickedColorInternal(
        Color value,
        bool raiseEvent,
        bool updateCurrentColorWhenIdle,
        bool clearFrozenPreview)
    {
        bool newHasPickedColor = !value.IsEmpty;
        bool changed =
            HasPickedColor != newHasPickedColor ||
            _pickedColor.ToArgb() != value.ToArgb();

        _pickedColor = value;
        HasPickedColor = newHasPickedColor;

        if(clearFrozenPreview)
        {
            SetFrozenPreview(null);
        }

        if(!IsPicking && updateCurrentColorWhenIdle)
        {
            SetCurrentColor(GetRestingDisplayColor());
        }

        if(changed && raiseEvent)
        {
            OnPickedColorChanged(EventArgs.Empty);
        }

        Invalidate();
    }

    private void SetFrozenPreview(Bitmap? bitmap)
    {
        if(ReferenceEquals(_pickedBitmap, bitmap))
        {
            return;
        }

        _pickedBitmap?.Dispose();
        _pickedBitmap = bitmap;
        Invalidate();
    }

    private void ClearFrozenPreviewIfIdle()
    {
        if(IsPicking || _pickedBitmap is null)
        {
            return;
        }

        SetFrozenPreview(null);
        SetCurrentColor(GetIdleSurfaceColor());
        Update();
    }

    private void SetCurrentColor(Color value)
    {
        if(CurrentColor.ToArgb() == value.ToArgb())
        {
            return;
        }

        CurrentColor = value;
        OnCurrentColorChanged(EventArgs.Empty);
    }

    private void UpdateIdleCurrentColorIfNeeded()
    {
        if(!IsPicking && _pickedBitmap is null)
        {
            SetCurrentColor(GetIdleSurfaceColor());
        }
    }

    private Color GetRestingDisplayColor()
    {
        return _pickedBitmap is not null && HasPickedColor
            ? _pickedColor
            : GetIdleSurfaceColor();
    }

    private Rectangle GetPreviewBounds()
    {
        return new Rectangle(
            Padding.Left,
            Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal),
            Math.Max(0, ClientSize.Height - Padding.Vertical));
    }

    private Rectangle GetContentBounds()
    {
        Rectangle previewBounds = GetPreviewBounds();
        int borderThickness = Math.Max(1, ScaleLogical(1));

        Rectangle contentBounds = Rectangle.Inflate(previewBounds, -borderThickness, -borderThickness);

        return contentBounds.Width > 0 && contentBounds.Height > 0
            ? contentBounds
            : Rectangle.Empty;
    }

    private (int Columns, int Rows) GetDynamicSampleGridSize()
    {
        Rectangle contentBounds = GetContentBounds();
        if(contentBounds.IsEmpty)
        {
            return (1, 1);
        }

        int scaledZoom = Math.Max(1, ScaleLogical(Zoom));

        int columns = Math.Max(1, (int)Math.Round(contentBounds.Width / (double)scaledZoom));
        int rows = Math.Max(1, (int)Math.Round(contentBounds.Height / (double)scaledZoom));

        if(columns > 1 && (columns & 1) == 0)
        {
            columns++;
        }

        if(rows > 1 && (rows & 1) == 0)
        {
            rows++;
        }

        return (columns, rows);
    }

    private void DrawHelpOverlayText(Graphics g, Rectangle bounds)
    {
        if(string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        const TextFormatFlags flags =
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.WordBreak |
            TextFormatFlags.TextBoxControl;

        int boxPaddingX = Math.Max(6, ScaleLogical(6));
        int boxPaddingY = Math.Max(4, ScaleLogical(4));
        int margin = Math.Max(8, ScaleLogical(8));

        int maxTextWidth = Math.Max(1, bounds.Width - (margin * 2) - (boxPaddingX * 2));
        Size proposedSize = new(maxTextWidth, int.MaxValue);

        Size textSize = TextRenderer.MeasureText(g, Text, Font, proposedSize, flags);

        int boxWidth = Math.Min(bounds.Width - (margin * 2), textSize.Width + (boxPaddingX * 2));
        int boxHeight = Math.Min(bounds.Height - (margin * 2), textSize.Height + (boxPaddingY * 2));

        if(boxWidth <= 0 || boxHeight <= 0)
        {
            return;
        }

        Rectangle boxBounds = new(
            bounds.Left + ((bounds.Width - boxWidth) / 2),
            bounds.Top + ((bounds.Height - boxHeight) / 2),
            boxWidth,
            boxHeight);

        Color boxBackColor = GetIdleSurfaceColor();
        Color textColor = GetContrastColor(boxBackColor);
        Color borderColor = textColor == Color.White
            ? Color.FromArgb(160, Color.White)
            : Color.FromArgb(160, Color.Black);

        using(SolidBrush boxBrush = new(boxBackColor))
        {
            g.FillRectangle(boxBrush, boxBounds);
        }

        using(Pen boxPen = new(borderColor, Math.Max(1, ScaleLogical(1))))
        {
            g.DrawRectangle(boxPen, boxBounds);
        }

        Rectangle textBounds = Rectangle.Inflate(boxBounds, -boxPaddingX, -boxPaddingY);

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textBounds,
            textColor,
            flags);
    }

    private void DrawPixelGrid(Graphics g, Rectangle bounds, Color gridColor, int columns, int rows)
    {
        if(columns <= 0 || rows <= 0)
        {
            return;
        }

        float cellWidth = bounds.Width / (float)columns;
        float cellHeight = bounds.Height / (float)rows;

        if(cellWidth < 2f || cellHeight < 2f)
        {
            return;
        }

        using Pen pen = new(Color.FromArgb(180, gridColor), Math.Max(1, ScaleLogical(1)))
        {
            DashStyle = DashStyle.Dot
        };

        for(int x = 1; x < columns; x++)
        {
            int px = bounds.Left + (x * bounds.Width / columns);
            g.DrawLine(pen, px, bounds.Top, px, bounds.Bottom);
        }

        for(int y = 1; y < rows; y++)
        {
            int py = bounds.Top + (y * bounds.Height / rows);
            g.DrawLine(pen, bounds.Left, py, bounds.Right, py);
        }
    }

    private void DrawCenterMarker(Graphics g, Rectangle bounds, Color outerColor, int columns, int rows)
    {
        int centerColumn = columns / 2;
        int centerRow = rows / 2;

        Rectangle cell = GetCellBounds(bounds, centerColumn, centerRow, columns, rows);

        int outerThickness = Math.Max(1, ScaleLogical(2));
        int innerThickness = Math.Max(1, ScaleLogical(1));
        int innerInset = Math.Max(1, ScaleLogical(2));

        Color innerColor = outerColor.ToArgb() == DarkGridColor.ToArgb()
            ? LightGridColor
            : DarkGridColor;

        using(Pen outerPen = new(outerColor, outerThickness))
        {
            g.DrawRectangle(outerPen, cell);
        }

        Rectangle inner = Rectangle.Inflate(cell, -innerInset, -innerInset);
        if(inner.Width > 0 && inner.Height > 0)
        {
            using Pen innerPen = new(innerColor, innerThickness);
            g.DrawRectangle(innerPen, inner);
        }
    }

    private Rectangle GetCellBounds(Rectangle bounds, int xIndex, int yIndex, int columns, int rows)
    {
        int left = bounds.Left + (xIndex * bounds.Width / columns);
        int top = bounds.Top + (yIndex * bounds.Height / rows);
        int right = bounds.Left + ((xIndex + 1) * bounds.Width / columns);
        int bottom = bounds.Top + ((yIndex + 1) * bounds.Height / rows);

        return Rectangle.FromLTRB(left, top, right - 1, bottom - 1);
    }

    private Bitmap? GetVisibleBitmap()
    {
        return IsPicking ? _sampleBitmap : _pickedBitmap;
    }

    private Color GetCurrentGridColor()
    {
        if(!UseAdaptiveGridColor)
        {
            return DarkGridColor;
        }

        Bitmap? visibleBitmap = GetVisibleBitmap();
        return visibleBitmap is not null
            ? GetAdaptiveGridColor(visibleBitmap, DarkGridColor, LightGridColor)
            : GetAdaptiveGridColor(GetVisibleSurfaceRepresentativeColor(), DarkGridColor, LightGridColor);
    }

    private Color GetVisibleSurfaceRepresentativeColor()
    {
        return IsPicking
            ? CurrentColor
            : _pickedBitmap is not null && HasPickedColor
                ? _pickedColor
                : GetIdleSurfaceColor();
    }

    private Color GetIdleSurfaceColor()
    {
        return BackColor.A > 0 ? BackColor : DarkGridColor;
    }

    private void PaintTransparentBackground(Graphics g)
    {
        if(Parent is null)
        {
            return;
        }

        GraphicsState state = g.Save();

        try
        {
            // Repaint the parent behind the control so transparency behaves as expected.
            g.TranslateTransform(-Left, -Top);

            using PaintEventArgs pea = new(g, Parent.ClientRectangle);
            InvokePaintBackground(Parent, pea);
            InvokePaint(Parent, pea);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private int ScaleLogical(int value)
    {
        return (int)Math.Round(value * (DeviceDpi / 96f));
    }

    private bool IsInDesigner()
    {
        return LicenseManager.UsageMode == LicenseUsageMode.Designtime
            || DesignMode
            || (Site?.DesignMode ?? false);
    }

    private static Color GetCenterPixelColor(Bitmap bitmap)
    {
        int centerX = bitmap.Width / 2;
        int centerY = bitmap.Height / 2;
        return bitmap.GetPixel(centerX, centerY);
    }

    private static Color GetAdaptiveGridColor(Bitmap bitmap, Color darkAreaColor, Color lightAreaColor)
    {
        // A light sample pass is enough here and far cheaper than scanning every pixel.
        int stepX = Math.Max(1, bitmap.Width / AdaptiveGridSampleTarget);
        int stepY = Math.Max(1, bitmap.Height / AdaptiveGridSampleTarget);

        double luminanceSum = 0;
        int count = 0;

        for(int y = 0; y < bitmap.Height; y += stepY)
        {
            for(int x = 0; x < bitmap.Width; x += stepX)
            {
                Color pixel = bitmap.GetPixel(x, y);
                luminanceSum += GetLuminance(pixel);
                count++;
            }
        }

        double averageLuminance = count == 0 ? 0 : luminanceSum / count;
        return averageLuminance < 128 ? darkAreaColor : lightAreaColor;
    }

    private static Color GetAdaptiveGridColor(Color surfaceColor, Color darkAreaColor, Color lightAreaColor)
    {
        return GetLuminance(surfaceColor) < 128 ? darkAreaColor : lightAreaColor;
    }

    private static double GetLuminance(Color color)
    {
        return (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
    }

    private static Color GetContrastColor(Color color)
    {
        return GetLuminance(color) < 140 ? Color.White : Color.Black;
    }

    private static string FormatHex(Color color)
    {
        return color.IsEmpty
            ? string.Empty
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

#if NET10_0_OR_GREATER
    private void ApplyFormScreenCaptureMode()
    {
        Form? form = FindForm();
        if(form is null)
        {
            return;
        }

        _previousScreenCaptureMode = form.FormScreenCaptureMode;

        form.FormScreenCaptureMode =
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                ? ScreenCaptureMode.HideWindow
                : ScreenCaptureMode.HideContent;
    }

    private void RestoreFormScreenCaptureMode()
    {
        Form? form = FindForm();
        if(form is null || _previousScreenCaptureMode is null)
        {
            return;
        }

        form.FormScreenCaptureMode = _previousScreenCaptureMode.Value;
        _previousScreenCaptureMode = null;
    }
#endif
}

public sealed class ScreenColorPickedEventArgs : EventArgs
{
    public ScreenColorPickedEventArgs(
        Color pickedColor,
        Color currentColor,
        Point screenLocation,
        string hexColor)
    {
        PickedColor = pickedColor;
        CurrentColor = currentColor;
        ScreenLocation = screenLocation;
        HexColor = hexColor;
    }

    /// <summary>
    /// Gets the committed colour.
    /// </summary>
    public Color PickedColor { get; }

    /// <summary>
    /// Gets the live colour at the moment the pick completed.
    /// </summary>
    public Color CurrentColor { get; }

    /// <summary>
    /// Gets the screen position that was sampled.
    /// </summary>
    public Point ScreenLocation { get; }

    /// <summary>
    /// Gets the committed colour formatted as a hexadecimal RGB string.
    /// </summary>
    public string HexColor { get; }
}