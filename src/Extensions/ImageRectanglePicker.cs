using System;
using System.Linq;
using System.Collections.Generic;
using OpenCV.Net;
using System.Reactive.Linq;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL;
using System.Drawing;
using Bonsai.Design;
using OpenTK;

namespace Bonsai.Vision.Design
{
    public class PublicImageRectanglePicker : ImageBox
    {
        Rect previous;
        Rect rectangle;
        const float LineWidth = 2;
        const double ScaleIncrement = 0.1;
        readonly CommandExecutor commandExecutor = new CommandExecutor();
        private List<Rect> regions = new List<Rect>();
        private bool labelRegions = false;
        private int selectedRegionIndex = -1;
        private const int MaxRegions = 2;

        public PublicImageRectanglePicker()
        {
            Canvas.KeyDown += Canvas_KeyDown;
            var lostFocus = Observable.FromEventPattern<EventArgs>(Canvas, "LostFocus").Select(e => e.EventArgs);
            var mouseMove = Observable.FromEventPattern<MouseEventArgs>(Canvas, "MouseMove").Select(e => e.EventArgs);
            var mouseDown = Observable.FromEventPattern<MouseEventArgs>(Canvas, "MouseDown").Select(e => e.EventArgs);
            var mouseUp = Observable.FromEventPattern<MouseEventArgs>(Canvas, "MouseUp").Select(e => e.EventArgs);
            var mouseLeftButtonUp = mouseUp.Where(evt => evt.Button == MouseButtons.Left);
            var mouseRightButtonUp = mouseUp.Where(evt => evt.Button == MouseButtons.Right &&
                                                          !MouseButtons.HasFlag(MouseButtons.Left));

            var mousePick = (from downEvt in mouseDown.Where(evt => Image != null && evt.Button == MouseButtons.Left)
                             let clickPoint = new OpenCV.Net.Point(downEvt.X, downEvt.Y)
                             let selectedIndex = GetRegionIndexAtPoint(clickPoint)
                             let origin = clickPoint
                             let isSelectingExisting = selectedIndex >= 0
                             let canCreateNew = regions.Count < MaxRegions
                             where isSelectingExisting || canCreateNew
                             select Observable.Defer(() =>
                             {
                                 Rect originalRegion = default(Rect);
                                 Rect originalCanvasRegion = default(Rect);
                                 
                                 if (selectedIndex >= 0)
                                 {
                                     selectedRegionIndex = selectedIndex;
                                     originalRegion = regions[selectedRegionIndex];
                                     originalCanvasRegion = CanvasRectangle(originalRegion);
                                 }
                                 else
                                 {
                                     selectedRegionIndex = -1;
                                     previous = rectangle;
                                 }
                                 Canvas.Invalidate();
                                 
                                 return (from moveEvt in mouseMove.TakeUntil(mouseLeftButtonUp.Merge(lostFocus))
                                         let displacementX = moveEvt.X - origin.X
                                         let displacementY = moveEvt.Y - origin.Y
                                         select isSelectingExisting
                                             ? new Rect(originalCanvasRegion.X + displacementX, originalCanvasRegion.Y + displacementY, originalRegion.Width, originalRegion.Height)
                                             : new Rect(origin.X, origin.Y, displacementX, displacementY))
                                         .Do(x =>
                                         {
                                             if (isSelectingExisting)
                                             {
                                                 var normalized = NormalizedRectangle(new Rect(x.X, x.Y, originalCanvasRegion.Width, originalCanvasRegion.Height));
                                                 normalized.Width = originalRegion.Width;
                                                 normalized.Height = originalRegion.Height;
                                                 regions[selectedRegionIndex] = FitRectangle(normalized);
                                             }
                                             else
                                             {
                                                 rectangle = NormalizedRectangle(x);
                                             }
                                             Canvas.Invalidate();
                                         })
                                         .TakeLast(1)
                                         .Do(x =>
                                         {
                                             if (!isSelectingExisting)
                                             {
                                                 rectangle.X = Math.Min(rectangle.X, rectangle.X + rectangle.Width);
                                                 rectangle.Y = Math.Min(rectangle.Y, rectangle.Y + rectangle.Height);
                                                 rectangle.Width = Math.Abs(rectangle.Width);
                                                 rectangle.Height = Math.Abs(rectangle.Height);
                                                 rectangle = ClipRectangle(rectangle);
                                                 
                                                 if (rectangle.Width > 0 && rectangle.Height > 0)
                                                 {
                                                     regions.Add(rectangle);
                                                     selectedRegionIndex = regions.Count - 1;
                                                     OnRegionsChanged(EventArgs.Empty);
                                                 }
                                             }
                                             else
                                             {
                                                 OnRegionsChanged(EventArgs.Empty);
                                             }
                                             Canvas.Invalidate();
                                         });
                             })).Switch();

            mousePick.Subscribe();
        }

        void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PageUp) ImageScale += ScaleIncrement;
            if (e.KeyCode == Keys.PageDown) ImageScale -= ScaleIncrement;
            if (e.Control && e.KeyCode == Keys.Z) commandExecutor.Undo();
            if (e.Control && e.KeyCode == Keys.Y) commandExecutor.Redo();
            
            // Delete selected region
            if (e.KeyCode == Keys.Delete && selectedRegionIndex >= 0 && selectedRegionIndex < regions.Count)
            {
                regions.RemoveAt(selectedRegionIndex);
                selectedRegionIndex = -1;
                OnRegionsChanged(EventArgs.Empty);
                Canvas.Invalidate();
            }
        }

        int GetRegionIndexAtPoint(OpenCV.Net.Point point)
        {
            for (int i = 0; i < regions.Count; i++)
            {
                var canvasRect = CanvasRectangle(regions[i]);
                if (IntersectRectangle(canvasRect, point.X, point.Y))
                {
                    return i;
                }
            }
            return -1;
        }

        void UpdateRectangle(Rect current, Rect previous)
        {
            commandExecutor.Execute(
            () =>
            {
                rectangle = current;
                OnRectangleChanged(EventArgs.Empty);
                OnRegionsChanged(EventArgs.Empty);
            },
            () =>
            {
                rectangle = previous;
                OnRectangleChanged(EventArgs.Empty);
                OnRegionsChanged(EventArgs.Empty);
            });
        }

        Rect ClipRectangle(Rect rect)
        {
            var clipX = rect.X < 0 ? -rect.X : 0;
            var clipY = rect.Y < 0 ? -rect.Y : 0;
            clipX += Math.Max(0, rect.X + rect.Width - Image.Width);
            clipY += Math.Max(0, rect.Y + rect.Height - Image.Height);

            rect.X = Math.Max(0, rect.X);
            rect.Y = Math.Max(0, rect.Y);
            rect.Width = rect.Width - clipX;
            rect.Height = rect.Height - clipY;
            return rect;
        }

        Rect FitRectangle(Rect rect)
        {
            rect.X += rect.X < 0 ? -rect.X : -Math.Max(0, rect.X + rect.Width - Image.Width);
            rect.Y += rect.Y < 0 ? -rect.Y : -Math.Max(0, rect.Y + rect.Height - Image.Height);
            return rect;
        }

        static bool IntersectRectangle(Rect rect, int x, int y)
        {
            return x >= rect.X && y >= rect.Y &&
                x < (rect.X + rect.Width) &&
                y < (rect.Y + rect.Height);
        }

        Rect CanvasRectangle(Rect rect)
        {
            return new Rect(
                (int)(rect.X * Canvas.Width / (float)Image.Width),
                (int)(rect.Y * Canvas.Height / (float)Image.Height),
                (int)(rect.Width * Canvas.Width / (float)Image.Width),
                (int)(rect.Height * Canvas.Height / (float)Image.Height));
        }

        Rect NormalizedRectangle(Rect rect)
        {
            return new Rect(
                (int)(rect.X * Image.Width / (float)Canvas.Width),
                (int)(rect.Y * Image.Height / (float)Canvas.Height),
                (int)(rect.Width * Image.Width / (float)Canvas.Width),
                (int)(rect.Height * Image.Height / (float)Canvas.Height));
        }

        Box2 DrawingRectangle(Rect rect)
        {
            var image = Image;
            if (image == null) return new Box2(0, 0, 0, 0);
            return new Box2(
                (rect.X * 2 / (float)image.Width) - 1,
                -((rect.Y * 2 / (float)image.Height) - 1),
                ((rect.X + rect.Width) * 2 / (float)image.Width) - 1,
                -(((rect.Y + rect.Height) * 2 / (float)image.Height) - 1));
        }

        public Rect Rectangle
        {
            get { return rectangle; }
            set { rectangle = value; }
        }

        public List<Rect> Regions
        {
            get { return regions; }
            set { regions = value; }
        }

        public bool LabelRegions
        {
            get { return labelRegions; }
            set { labelRegions = value; }
        }

        public event EventHandler RectangleChanged;
        public event EventHandler RegionsChanged;

        protected virtual void OnRectangleChanged(EventArgs e)
        {
            if (RectangleChanged != null)
            {
                RectangleChanged(this, e);
            }
        }

        protected virtual void OnRegionsChanged(EventArgs e)
        {
            if (RegionsChanged != null)
            {
                RegionsChanged(this, e);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            GL.LineWidth(LineWidth);
            base.OnLoad(e);
        }

        protected override void OnRenderFrame(EventArgs e)
        {
            GL.Color3(Color.White);
            base.OnRenderFrame(e);

            GL.Disable(EnableCap.Texture2D);
            
            // Draw all regions from the Regions list with color coding
            if (regions != null && regions.Count > 0)
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    var region = regions[i];
                    
                    // Set color based on index: Blue for 0, Orange for 1
                    if (i == 0)
                    {
                        GL.Color3(Color.Blue);
                    }
                    else if (i == 1)
                    {
                        GL.Color3(Color.Orange);
                    }
                    else
                    {
                        GL.Color3(Color.Red);
                    }
                    
                    // Draw thicker line if selected
                    if (i == selectedRegionIndex)
                    {
                        GL.LineWidth(LineWidth * 2);
                    }
                    else
                    {
                        GL.LineWidth(LineWidth);
                    }
                    
                    GL.Begin(PrimitiveType.LineLoop);
                    var drawingRect = DrawingRectangle(region);
                    GL.Vertex2(drawingRect.Left, drawingRect.Top);
                    GL.Vertex2(drawingRect.Right, drawingRect.Top);
                    GL.Vertex2(drawingRect.Right, drawingRect.Bottom);
                    GL.Vertex2(drawingRect.Left, drawingRect.Bottom);
                    GL.End();
                }
                
                // Reset line width
                GL.LineWidth(LineWidth);
            }
        }
    }
}