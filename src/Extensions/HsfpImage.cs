using System;
using System.Collections.Generic;
using System.Numerics;
using Hexa.NET.ImGui;
using OpenCV.Net;
using OpenTK.Graphics.OpenGL4;

/// <summary>
/// The colormaps offered for the camera image, as key colors evenly spaced from the lowest to the highest value
/// (the same keys as ImPlot's colormaps). They need no ImPlot context.
/// </summary>
static class HsfpColormaps
{
    public static readonly string[] Names = { "Gray", "Viridis", "Plasma", "Hot", "Cool", "Jet", "Twilight" };

    static readonly int[][] Keys =
    {
        new[] { 0x000000, 0xffffff },
        new[] { 0x440154, 0x472c7a, 0x3b518b, 0x2c718e, 0x21908d, 0x27ad81, 0x5cc863, 0xaadc32, 0xfde725 },
        new[] { 0x0d0887, 0x4b03a1, 0x7d03a8, 0xa82296, 0xcb4679, 0xe56b5d, 0xf89441, 0xfdc328, 0xf0f921 },
        new[] { 0x000000, 0x9a0000, 0xff2800, 0xffa000, 0xffff3c, 0xffffff },
        new[] { 0x00ffff, 0xff00ff },
        new[] { 0x00007f, 0x0000ff, 0x007fff, 0x00ffff, 0x7fff7f, 0xffff00, 0xff7f00, 0xff0000, 0x7f0000 },
        new[] { 0xe2d9e2, 0xa6bfca, 0x6d90c0, 0x5f58b0, 0x531e7c, 0x2f1436, 0x64194b, 0x9f3c50, 0xc0755e, 0xd0b39e, 0xe2d9e2 },
    };

    /// <summary>Color of the colormap at <paramref name="t"/> (0 to 1), interpolated between its keys.</summary>
    public static Vector4 Sample(int index, float t)
    {
        var keys = Keys[Math.Max(0, Math.Min(index, Keys.Length - 1))];
        var x = Math.Max(0f, Math.Min(1f, t)) * (keys.Length - 1);
        var i = Math.Min(keys.Length - 2, (int)Math.Floor(x));
        var f = x - i;
        var a = ToColor(keys[i]);
        var b = ToColor(keys[i + 1]);
        return a + (b - a) * f;
    }

    static Vector4 ToColor(int rgb)
    {
        return new Vector4(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
    }

    /// <summary>Draws a vertical colorbar at the cursor, with the high value at the top and five labelled ticks.</summary>
    public static void Colorbar(int index, double lo, double hi, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var barWidth = Math.Min(HsfpDpi.Px(12f), size.X * 0.3f);
        var top = origin.Y + ImGui.GetTextLineHeight() * 0.5f;
        var bottom = origin.Y + size.Y - ImGui.GetTextLineHeight() * 0.5f;
        const int Segments = 32;
        for (int k = 0; k < Segments; k++)
        {
            var y0 = bottom - (bottom - top) * k / Segments;
            var y1 = bottom - (bottom - top) * (k + 1) / Segments;
            var c0 = ImGui.ColorConvertFloat4ToU32(Sample(index, k / (float)Segments));
            var c1 = ImGui.ColorConvertFloat4ToU32(Sample(index, (k + 1) / (float)Segments));
            drawList.AddRectFilledMultiColor(new Vector2(origin.X, y1), new Vector2(origin.X + barWidth, y0), c1, c1, c0, c0);
        }
        drawList.AddRect(new Vector2(origin.X, top), new Vector2(origin.X + barWidth, bottom), ImGui.GetColorU32(ImGuiCol.Border));
        var ticks = (bottom - top) > ImGui.GetTextLineHeight() * 6f ? 5 : 2;
        for (int k = 0; k < ticks; k++)
        {
            var t = k / (float)(ticks - 1);
            var y = bottom - (bottom - top) * t;
            var value = lo + (hi - lo) * t;
            var text = value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
            drawList.AddLine(new Vector2(origin.X + barWidth, y), new Vector2(origin.X + barWidth + HsfpDpi.Px(3f), y), ImGui.GetColorU32(ImGuiCol.Border));
            drawList.AddText(new Vector2(origin.X + barWidth + HsfpDpi.Px(5f), y - ImGui.GetTextLineHeight() * 0.5f), ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
        }
        ImGui.Dummy(size);
    }

    /// <summary>A combo box selecting an entry of <see cref="Names"/>. Returns true when it changes.</summary>
    public static bool Combo(string id, ref int index, float width)
    {
        var changed = false;
        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo(id, Names[index]))
        {
            for (int i = 0; i < Names.Length; i++)
            {
                if (ImGui.Selectable(Names[i], i == index))
                {
                    changed = i != index;
                    index = i;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}

/// <summary>
/// A camera image drawn with a colormap between two limits. The raw pixel values are uploaded to an OpenGL
/// texture as they are, and a shader maps them through the colormap, so changing the limits or the colormap
/// costs nothing. Create, update and draw it from the ImGui render thread.
/// </summary>
/// <remarks>
/// The shader is set with ImGui draw callbacks around the image, and reads the projection and vertex layout
/// of the ImGui OpenGL3 backend's own program, then restores it. If the shader cannot be built, the image is
/// drawn by the backend, without the colormap.
/// </remarks>
class HsfpColormapImage
{
    const int LutSize = 256;

    // The draw callbacks are static (and kept alive here); the image is found by an id passed as callback data.
    static readonly ImDrawCallback BeginCallback = OnBegin;
    static readonly ImDrawCallback EndCallback = OnEnd;
    static readonly Dictionary<int, HsfpColormapImage> Registry = new Dictionary<int, HsfpColormapImage>();
    static int nextId;

    readonly int id;
    int textureId;
    int lutId;
    int lutColormap = -1;
    int program;
    bool programFailed;
    int locProj, locTexture, locLut, locScale, locLo, locHi;
    int previousProgram;
    readonly float[] projection = new float[16];
    float valueScale = 1f;
    float drawLo, drawHi = 1f;

    public HsfpColormapImage()
    {
        lock (Registry)
        {
            id = ++nextId;
            Registry[id] = this;
        }
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsValid { get { return textureId != 0; } }

    /// <summary>Lowest and highest pixel values of the last image uploaded.</summary>
    public double DataMin { get; private set; }

    public double DataMax { get; private set; }

    public unsafe void Update(IplImage image)
    {
        if (image == null) return;
        IplImage upload = image;
        try
        {
            if (image.Channels != 1)
            {
                upload = new IplImage(image.Size, image.Depth, 1);
                CV.CvtColor(image, upload, ColorConversion.Bgr2Gray);
            }
            if (upload.Depth != IplDepth.U8 && upload.Depth != IplDepth.U16 && upload.Depth != IplDepth.F32)
            {
                var converted = new IplImage(upload.Size, IplDepth.F32, 1);
                CV.Convert(upload, converted);
                if (upload != image) upload.Dispose();
                upload = converted;
            }

            double min, max;
            Point minLoc, maxLoc;
            CV.MinMaxLoc(upload, out min, out max, out minLoc, out maxLoc);
            DataMin = min;
            DataMax = max;

            PixelInternalFormat internalFormat;
            PixelType type;
            int bytesPerPixel;
            if (upload.Depth == IplDepth.U8) { internalFormat = PixelInternalFormat.R8; type = PixelType.UnsignedByte; bytesPerPixel = 1; valueScale = 255f; }
            else if (upload.Depth == IplDepth.U16) { internalFormat = PixelInternalFormat.R16; type = PixelType.UnsignedShort; bytesPerPixel = 2; valueScale = 65535f; }
            else { internalFormat = PixelInternalFormat.R32f; type = PixelType.Float; bytesPerPixel = 4; valueScale = 1f; }

            var allocate = upload.Width != Width || upload.Height != Height;
            if (textureId == 0)
            {
                GL.GenTextures(1, out textureId);
                GL.BindTexture(TextureTarget.Texture2D, textureId);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                // Gray if the image is drawn without the shader.
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int)All.Red);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int)All.Red);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA, (int)All.One);
                allocate = true;
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, textureId);
            }

            var step = upload.WidthStep;
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, step % 4 == 0 ? 4 : step % 2 == 0 ? 2 : 1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, step / bytesPerPixel);
            if (allocate || internalFormat != currentFormat)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, upload.Width, upload.Height, 0, PixelFormat.Red, type, upload.ImageData);
                currentFormat = internalFormat;
            }
            else
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, upload.Width, upload.Height, PixelFormat.Red, type, upload.ImageData);
            }
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            Width = upload.Width;
            Height = upload.Height;
            GC.KeepAlive(upload);
        }
        finally
        {
            if (upload != image) upload.Dispose();
        }
    }

    PixelInternalFormat currentFormat;

    /// <summary>
    /// Draws the image at the cursor, mapping <paramref name="lo"/> to the first color of the colormap and
    /// <paramref name="hi"/> to the last.
    /// </summary>
    public unsafe void Draw(Vector2 size, double lo, double hi, int colormap)
    {
        if (textureId == 0)
        {
            ImGui.Dummy(size);
            return;
        }
        UpdateLut(colormap);
        drawLo = (float)lo;
        drawHi = (float)hi;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCallback(BeginCallback, (void*)(IntPtr)id);
        ImGui.Image(new ImTextureRef(texId: new ImTextureID(unchecked((ulong)(uint)textureId))), size);
        drawList.AddCallback(EndCallback, (void*)(IntPtr)id);
    }

    void UpdateLut(int colormap)
    {
        if (lutId != 0 && lutColormap == colormap) return;
        var data = new byte[LutSize * 4];
        for (int i = 0; i < LutSize; i++)
        {
            var color = HsfpColormaps.Sample(colormap, i / (float)(LutSize - 1));
            data[i * 4 + 0] = (byte)Math.Round(Clamp01(color.X) * 255);
            data[i * 4 + 1] = (byte)Math.Round(Clamp01(color.Y) * 255);
            data[i * 4 + 2] = (byte)Math.Round(Clamp01(color.Z) * 255);
            data[i * 4 + 3] = 255;
        }
        if (lutId == 0)
        {
            GL.GenTextures(1, out lutId);
            GL.BindTexture(TextureTarget.Texture2D, lutId);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        }
        else
        {
            GL.BindTexture(TextureTarget.Texture2D, lutId);
        }
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, LutSize, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
        lutColormap = colormap;
    }

    static float Clamp01(float value)
    {
        return Math.Max(0f, Math.Min(1f, value));
    }

    static unsafe HsfpColormapImage FromCommand(IntPtr command)
    {
        var id = (int)(long)((ImDrawCmd*)command)->UserCallbackData;
        HsfpColormapImage image;
        lock (Registry)
        {
            return Registry.TryGetValue(id, out image) ? image : null;
        }
    }

    static void OnBegin(IntPtr drawList, IntPtr command)
    {
        var image = FromCommand(command);
        if (image != null) image.Begin();
    }

    static void OnEnd(IntPtr drawList, IntPtr command)
    {
        var image = FromCommand(command);
        if (image != null) image.End();
    }

    void Begin()
    {
        int current;
        GL.GetInteger(GetPName.CurrentProgram, out current);
        previousProgram = current;
        if (program == 0 && !programFailed && current != 0) Build(current);
        if (program == 0) return;
        var projLocation = GL.GetUniformLocation(current, "ProjMtx");
        if (projLocation >= 0) GL.GetUniform(current, projLocation, projection);
        GL.UseProgram(program);
        GL.UniformMatrix4(locProj, 1, false, projection);
        GL.Uniform1(locTexture, 0);
        GL.Uniform1(locLut, 1);
        GL.Uniform1(locScale, valueScale);
        GL.Uniform1(locLo, drawLo);
        GL.Uniform1(locHi, drawHi);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, lutId);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    void End()
    {
        if (program != 0) GL.UseProgram(previousProgram);
    }

    const string VertexSource =
        "uniform mat4 ProjMtx;\n" +
        "in vec2 Position;\n" +
        "in vec2 UV;\n" +
        "in vec4 Color;\n" +
        "out vec2 Frag_UV;\n" +
        "out vec4 Frag_Color;\n" +
        "void main()\n" +
        "{\n" +
        "    Frag_UV = UV;\n" +
        "    Frag_Color = Color;\n" +
        "    gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);\n" +
        "}\n";

    const string FragmentSource =
        "uniform sampler2D Texture;\n" +
        "uniform sampler2D Lut;\n" +
        "uniform float Scale;\n" +
        "uniform float Lo;\n" +
        "uniform float Hi;\n" +
        "in vec2 Frag_UV;\n" +
        "in vec4 Frag_Color;\n" +
        "out vec4 Out_Color;\n" +
        "void main()\n" +
        "{\n" +
        "    float value = texture(Texture, Frag_UV).r * Scale;\n" +
        "    float t = clamp((value - Lo) / max(Hi - Lo, 1e-6), 0.0, 1.0);\n" +
        "    Out_Color = vec4(texture(Lut, vec2(t, 0.5)).rgb, 1.0) * Frag_Color;\n" +
        "}\n";

    // Builds the colormap program with the same vertex attribute locations as the backend's program, so that
    // it reads the backend's vertex array.
    void Build(int backendProgram)
    {
        foreach (var version in new[] { "#version 330 core\n", "#version 130\n" })
        {
            var built = TryBuild(version, backendProgram);
            if (built != 0)
            {
                program = built;
                locProj = GL.GetUniformLocation(program, "ProjMtx");
                locTexture = GL.GetUniformLocation(program, "Texture");
                locLut = GL.GetUniformLocation(program, "Lut");
                locScale = GL.GetUniformLocation(program, "Scale");
                locLo = GL.GetUniformLocation(program, "Lo");
                locHi = GL.GetUniformLocation(program, "Hi");
                return;
            }
        }
        programFailed = true;
    }

    static int TryBuild(string version, int backendProgram)
    {
        var vertex = Compile(ShaderType.VertexShader, version + VertexSource);
        var fragment = Compile(ShaderType.FragmentShader, version + FragmentSource);
        if (vertex == 0 || fragment == 0)
        {
            if (vertex != 0) GL.DeleteShader(vertex);
            if (fragment != 0) GL.DeleteShader(fragment);
            return 0;
        }
        var result = GL.CreateProgram();
        GL.AttachShader(result, vertex);
        GL.AttachShader(result, fragment);
        foreach (var attribute in new[] { "Position", "UV", "Color" })
        {
            var location = GL.GetAttribLocation(backendProgram, attribute);
            if (location >= 0) GL.BindAttribLocation(result, location, attribute);
        }
        GL.BindFragDataLocation(result, 0, "Out_Color");
        GL.LinkProgram(result);
        GL.DetachShader(result, vertex);
        GL.DetachShader(result, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        int linked;
        GL.GetProgram(result, GetProgramParameterName.LinkStatus, out linked);
        if (linked == 0)
        {
            GL.DeleteProgram(result);
            return 0;
        }
        return result;
    }

    static int Compile(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        int compiled;
        GL.GetShader(shader, ShaderParameter.CompileStatus, out compiled);
        if (compiled == 0)
        {
            GL.DeleteShader(shader);
            return 0;
        }
        return shader;
    }
}

/// <summary>
/// Display limits of an image or a plot axis: automatic (fit to the data), or a fixed min and max. Draws as a
/// small button showing the current mode, which opens a popup to edit it.
/// </summary>
class HsfpLimits
{
    public bool Auto = true;
    public double Min;
    public double Max = 1;

    /// <summary>Range of the data last shown, used by "Use current range" and shown in auto mode.</summary>
    public double CurrentMin;

    public double CurrentMax = 1;

    public void Observe(double min, double max)
    {
        if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max)) return;
        CurrentMin = min;
        CurrentMax = max;
    }

    /// <summary>Limits to draw with: the fixed ones, or the current range of the data in auto mode.</summary>
    public double Lo { get { return Auto ? CurrentMin : Min; } }

    public double Hi { get { return Auto ? CurrentMax : Max; } }

    /// <summary>Draws the button and its popup. Returns true when the limits change.</summary>
    public bool DrawButton(string id, string label)
    {
        var changed = false;
        var text = Auto ? label + ": Auto" : string.Format("{0}: {1} to {2}", label, Format(Min), Format(Max));
        var color = Auto ? HsfpUi.Muted : HsfpUi.Warning;
        if (HsfpUi.Pill(text + "##" + id, color, false)) ImGui.OpenPopup("##limits" + id);
        ImGui.SetItemTooltip(Auto ? "Limits fit the data. Click to set fixed limits." : "Fixed limits. Click to edit, or to go back to Auto.");
        if (ImGui.BeginPopup("##limits" + id))
        {
            HsfpUi.SectionHeader(label + " limits");
            if (ImGui.RadioButton("Auto##" + id, Auto))
            {
                changed |= !Auto;
                Auto = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Fixed##" + id, !Auto))
            {
                if (Auto)
                {
                    Min = CurrentMin;
                    Max = CurrentMax;
                    changed = true;
                }
                Auto = false;
            }
            var width = HsfpDpi.Px(110f);
            ImGui.SetNextItemWidth(width);
            var min = Auto ? CurrentMin : Min;
            if (ImGui.InputDouble("Min##" + id, ref min, 0, 0, "%.6g"))
            {
                Min = min;
                if (Auto) Max = CurrentMax;
                Auto = false;
                changed = true;
            }
            ImGui.SetNextItemWidth(width);
            var max = Auto ? CurrentMax : Max;
            if (ImGui.InputDouble("Max##" + id, ref max, 0, 0, "%.6g"))
            {
                Max = max;
                if (Auto) Min = CurrentMin;
                Auto = false;
                changed = true;
            }
            if (ImGui.Button("Use current range##" + id))
            {
                Min = CurrentMin;
                Max = CurrentMax;
                Auto = false;
                changed = true;
            }
            ImGui.SetItemTooltip(string.Format("Fix the limits to the range shown now ({0} to {1}).", Format(CurrentMin), Format(CurrentMax)));
            ImGui.EndPopup();
        }
        if (!Auto && Max <= Min) Max = Min + 1e-6;
        return changed;
    }

    /// <summary>Width of the button drawn by <see cref="DrawButton"/>.</summary>
    public float ButtonWidth(string label)
    {
        var text = Auto ? label + ": Auto" : string.Format("{0}: {1} to {2}", label, Format(Min), Format(Max));
        return ImGui.CalcTextSize(text).X + 2f * HsfpDpi.FramePadding.X;
    }

    static string Format(double value)
    {
        return value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
    }
}
