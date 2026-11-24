using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;



/// <summary>
/// One-time look of an ImGui context hosting HSFP visualizers (Segoe UI, compact rounded style),
/// then the monitor DPI scale. Call once at the start of each visualizer's draw.
/// </summary>
static class HsfpStyle
{
    const float FontSize = 13f;
    static readonly string FontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
    // Arbitrary tag stored in io.UserData ("HSFP"), so the style is set once per context.
    static readonly IntPtr StyledMarker = new IntPtr(0x48534650);

    public static void Apply()
    {
        unsafe
        {
            // Disable native assertions for recoverable ImGui errors.
            ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0;
            if ((IntPtr)ImGui.GetIO().Handle->UserData != StyledMarker)
            {
                ImGui.GetIO().Handle->UserData = (void*)StyledMarker;
                SetStyle();
                LoadFont();
            }
        }
        HsfpDpi.Apply();
        // The ImGui host resets the colors to its light theme, so the palette is applied every frame.
        ApplyColors();
        PaintBackground();
    }

    static readonly Vector4 Background = Rgb(0x24, 0x26, 0x2b);
    static readonly System.Collections.Generic.Dictionary<IntPtr, int> paintedFrames = new System.Collections.Generic.Dictionary<IntPtr, int>();

    // The host window's background is drawn before the visualizers run, with the host colors;
    // cover it once per frame and context with the HSFP background.
    static unsafe void PaintBackground()
    {
        var context = (IntPtr)ImGui.GetCurrentContext().Handle;
        var frame = ImGui.GetFrameCount();
        lock (paintedFrames)
        {
            int painted;
            if (paintedFrames.TryGetValue(context, out painted) && painted == frame) return;
            paintedFrames[context] = frame;
        }
        var pos = ImGui.GetWindowPos();
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + ImGui.GetWindowSize(), ImGui.ColorConvertFloat4ToU32(Background));
    }

    static unsafe void ApplyColors()
    {
        ImGui.StyleColorsDark();
        var plot = Hexa.NET.ImPlot.ImPlot.GetCurrentContext();
        if (!plot.IsNull) Hexa.NET.ImPlot.ImPlot.StyleColorsDark();
        var style = ImGui.GetStyle().Handle;
        var colors = &style->Colors_0;
        colors[(int)ImGuiCol.WindowBg] = Rgb(0x24, 0x26, 0x2b);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0f, 0f, 0f, 0f);
        colors[(int)ImGuiCol.PopupBg] = Rgb(0x1f, 0x20, 0x24);
        colors[(int)ImGuiCol.Border] = Rgb(0x33, 0x35, 0x3b);
        colors[(int)ImGuiCol.FrameBg] = Rgb(0x1c, 0x1d, 0x21);
        colors[(int)ImGuiCol.FrameBgHovered] = Rgb(0x2a, 0x2c, 0x31);
        colors[(int)ImGuiCol.FrameBgActive] = Rgb(0x30, 0x33, 0x39);
        colors[(int)ImGuiCol.Button] = Rgb(0x3a, 0x3d, 0x44);
        colors[(int)ImGuiCol.ButtonHovered] = Rgb(0x48, 0x4c, 0x55);
        colors[(int)ImGuiCol.ButtonActive] = Rgb(0x2f, 0x6f, 0xde);
        colors[(int)ImGuiCol.CheckMark] = Rgb(0x6c, 0xa0, 0xff);
        colors[(int)ImGuiCol.Header] = Rgb(0x30, 0x33, 0x39);
        colors[(int)ImGuiCol.HeaderHovered] = Rgb(0x3a, 0x3d, 0x44);
        colors[(int)ImGuiCol.HeaderActive] = Rgb(0x2f, 0x6f, 0xde);
        colors[(int)ImGuiCol.Tab] = Rgb(0x2a, 0x2c, 0x31);
        colors[(int)ImGuiCol.TabHovered] = Rgb(0x3a, 0x3d, 0x44);
        colors[(int)ImGuiCol.TabSelected] = Rgb(0x3a, 0x3d, 0x44);
        colors[(int)ImGuiCol.TabSelectedOverline] = Rgb(0x6c, 0xa0, 0xff);
        colors[(int)ImGuiCol.TabDimmed] = Rgb(0x2a, 0x2c, 0x31);
        colors[(int)ImGuiCol.TabDimmedSelected] = Rgb(0x30, 0x33, 0x39);
        colors[(int)ImGuiCol.TableHeaderBg] = Rgb(0x1c, 0x1d, 0x21);
        colors[(int)ImGuiCol.TableRowBg] = new Vector4(0f, 0f, 0f, 0f);
        colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.025f);
        colors[(int)ImGuiCol.TableBorderLight] = Rgb(0x33, 0x35, 0x3b);
        colors[(int)ImGuiCol.Separator] = Rgb(0x33, 0x35, 0x3b);
        colors[(int)ImGuiCol.Text] = Rgb(0xe8, 0xea, 0xed);
        colors[(int)ImGuiCol.TextDisabled] = Rgb(0x9a, 0xa0, 0xa6);
        style->WindowMenuButtonPosition = ImGuiDir.None;
        style->WindowBorderSize = 0f;
    }

    static unsafe void SetStyle()
    {
        var style = ImGui.GetStyle().Handle;
        style->WindowPadding = new Vector2(8f, 8f);
        style->FramePadding = new Vector2(6f, 3f);
        style->ItemSpacing = new Vector2(6f, 5f);
        style->ItemInnerSpacing = new Vector2(4f, 4f);
        style->CellPadding = new Vector2(6f, 3f);
        style->ScrollbarSize = 12f;
        style->GrabMinSize = 10f;
        style->FrameRounding = 3f;
        style->GrabRounding = 3f;
        style->PopupRounding = 3f;
        style->TabRounding = 3f;
        style->ChildRounding = 4f;
    }

    static Vector4 Rgb(int r, int g, int b)
    {
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }

    static unsafe void LoadFont()
    {
        if (!File.Exists(FontPath)) return;
        // ImGuiIOPtr.Fonts is a ref property, which Bonsai's script compiler rejects (CS0570).
        var fonts = new ImFontAtlasPtr(ImGui.GetIO().Handle->Fonts);
        var font = fonts.AddFontFromFileTTF(FontPath, FontSize);
        if (font != null) ImGui.GetIO().Handle->FontDefault = font;
    }
}

/// <summary>
/// Scales the current ImGui context to the monitor DPI. Sizes in the HSFP visualizers are written
/// for 96 DPI and converted with <see cref="Px"/>.
/// </summary>
static class HsfpDpi
{
    const float BaseDpi = 96f;

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    public static float Scale
    {
        get { unsafe { return Math.Max(0.25f, ImGui.GetStyle().Handle->FontScaleDpi); } }
    }

    public static Vector2 ItemSpacing
    {
        get { unsafe { return ImGui.GetStyle().Handle->ItemSpacing; } }
    }

    public static Vector2 FramePadding
    {
        get { unsafe { return ImGui.GetStyle().Handle->FramePadding; } }
    }

    public static void Apply()
    {
        var target = Query();
        unsafe
        {
            var style = ImGui.GetStyle();
            var current = Math.Max(0.25f, style.Handle->FontScaleDpi);
            if (Math.Abs(target - current) < 0.01f) return;
            style.ScaleAllSizes(target / current);
            style.Handle->FontScaleDpi = target;
        }
    }

    public static float Px(float logicalPixels)
    {
        return logicalPixels * Scale;
    }

    public static float FitWidth(string text, float minLogicalWidth)
    {
        return Math.Max(Px(minLogicalWidth), ImGui.CalcTextSize(text).X + 2f * FramePadding.X);
    }

    static float Query()
    {
        uint dpi = 0;
        try
        {
            IntPtr hwnd;
            unsafe { hwnd = (IntPtr)ImGui.GetMainViewport().Handle->PlatformHandleRaw; }
            dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : GetDpiForSystem();
        }
        catch (EntryPointNotFoundException)
        {
        }
        return dpi > 0 ? dpi / BaseDpi : 1f;
    }
}

/// <summary>Colors and small widgets shared by the HSFP visualizers.</summary>
static class HsfpUi
{
    public static readonly Vector4 Muted = new Vector4(0.60f, 0.63f, 0.66f, 1f);
    public static readonly Vector4 Good = new Vector4(0.29f, 0.87f, 0.50f, 1f);
    public static readonly Vector4 Bad = new Vector4(1.00f, 0.45f, 0.45f, 1f);
    public static readonly Vector4 Warning = new Vector4(0.98f, 0.75f, 0.25f, 1f);
    public static readonly Vector4 StartColor = new Vector4(0.18f, 0.44f, 0.87f, 1f);
    public static readonly Vector4 StopColor = new Vector4(0.76f, 0.25f, 0.29f, 1f);
    public static readonly Vector4 TileColor = new Vector4(0.11f, 0.11f, 0.13f, 1f);

    static readonly Vector4[] FiberPalette =
    {
        new Vector4(0.91f, 0.92f, 0.93f, 1f),
        new Vector4(0.96f, 0.62f, 0.04f, 1f),
        new Vector4(0.93f, 0.40f, 0.80f, 1f),
        new Vector4(0.20f, 0.83f, 0.75f, 1f),
        new Vector4(0.55f, 0.65f, 1.00f, 1f),
        new Vector4(0.75f, 0.85f, 0.35f, 1f),
    };

    /// <summary>Color of the n-th fiber; cycles through a palette distinct from the laser colors.</summary>
    public static Vector4 FiberColor(int index)
    {
        return FiberPalette[((index % FiberPalette.Length) + FiberPalette.Length) % FiberPalette.Length];
    }

    static readonly Vector4[] SpectrumPalette =
    {
        new Vector4(0.35f, 0.85f, 1.00f, 1f),
        new Vector4(1.00f, 0.88f, 0.30f, 1f),
        new Vector4(1.00f, 0.55f, 0.45f, 1f),
        new Vector4(0.72f, 0.58f, 1.00f, 1f),
        new Vector4(0.50f, 1.00f, 0.70f, 1f),
        new Vector4(1.00f, 0.70f, 0.90f, 1f),
    };

    /// <summary>Color of the n-th spectrum ROI; distinct from the fiber colors.</summary>
    public static Vector4 SpectrumColor(int index)
    {
        return SpectrumPalette[((index % SpectrumPalette.Length) + SpectrumPalette.Length) % SpectrumPalette.Length];
    }

    public static uint WithAlpha(Vector4 color, float alpha)
    {
        return ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alpha));
    }

    static readonly Vector4[] ChannelPalette =
    {
        new Vector4(0.85f, 0.85f, 0.85f, 1f),
        new Vector4(0.80f, 0.72f, 0.60f, 1f),
        new Vector4(0.60f, 0.75f, 0.80f, 1f),
        new Vector4(0.78f, 0.65f, 0.80f, 1f),
        new Vector4(0.70f, 0.80f, 0.65f, 1f),
        new Vector4(0.82f, 0.68f, 0.68f, 1f),
        new Vector4(0.68f, 0.70f, 0.85f, 1f),
    };

    /// <summary>
    /// Color of a cuTTLefish channel: the color of its light source's wavelength, or, if the wavelength is not
    /// known, a neutral tint per channel.
    /// </summary>
    public static Vector4 ChannelColor(AindPhysiologyHsfp.LightSource source, int channel)
    {
        if (source != null && source.Wavelength.HasValue) return WavelengthColor(source.Wavelength.Value);
        return source != null ? ChannelPalette[((channel % ChannelPalette.Length) + ChannelPalette.Length) % ChannelPalette.Length] : Muted;
    }

    /// <summary>
    /// Approximate color of a wavelength (nm), brightened to read well on a dark background.
    /// Any wavelength works, so adding a light source to the rig needs no change here.
    /// </summary>
    public static Vector4 WavelengthColor(double wavelength)
    {
        double r = 0, g = 0, b = 0;
        var w = wavelength;
        if (w < 440) { r = -(w - 440) / (440 - 380); b = 1; }
        else if (w < 490) { g = (w - 440) / (490 - 440); b = 1; }
        else if (w < 510) { g = 1; b = -(w - 510) / (510 - 490); }
        else if (w < 580) { r = (w - 510) / (580 - 510); g = 1; }
        else if (w < 645) { r = 1; g = -(w - 645) / (645 - 580); }
        else { r = 1; }
        r = Math.Max(0, Math.Min(1, r));
        g = Math.Max(0, Math.Min(1, g));
        b = Math.Max(0, Math.Min(1, b));
        // Lift towards white so deep violet/red stay legible on a dark background.
        const double lift = 0.35;
        return new Vector4((float)(r + (1 - r) * lift), (float)(g + (1 - g) * lift), (float)(b + (1 - b) * lift), 1f);
    }

    public static void SectionHeader(string text)
    {
        ImGui.TextColored(Muted, text.ToUpperInvariant());
    }

    public static void PushButtonColor(Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Math.Min(1f, color.X + 0.08f), Math.Min(1f, color.Y + 0.08f), Math.Min(1f, color.Z + 0.08f), 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.X * 0.85f, color.Y * 0.85f, color.Z * 0.85f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }

    public static void PopButtonColor()
    {
        ImGui.PopStyleColor(4);
    }

    /// <summary>A toggle-like pill button. Returns true when clicked.</summary>
    public static bool Pill(string label, Vector4 color, bool selected)
    {
        var text = selected ? new Vector4(0.07f, 0.07f, 0.08f, 1f) : color;
        var fill = selected ? color : new Vector4(0f, 0f, 0f, 0f);
        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selected ? color : new Vector4(color.X, color.Y, color.Z, 0.20f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleColor(ImGuiCol.Border, color);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, HsfpDpi.Px(12f));
        var clicked = ImGui.Button(label);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
        return clicked;
    }

    /// <summary>A small labelled value tile, like a dashboard stat.</summary>
    public static void Tile(string id, string label, string value, Vector4 valueColor, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TileColor);
        ImGui.BeginChild(id, size, ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);
        ImGui.TextColored(Muted, label);
        ImGui.TextColored(valueColor, value);
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }
}

