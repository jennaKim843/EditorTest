using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace InnoPVManagementSystem.Modules.CompareMerge.Services
{
    //internal class TextMarkerService
    public interface ITextMarker
    {
        int StartOffset { get; }
        int Length { get; }
        int EndOffset { get; }
        Color? BackgroundColor { get; set; }
        Color? ForegroundColor { get; set; }
        TextMarkerType MarkerType { get; set; }
        Color? MarkerColor { get; set; }
    }

    public enum TextMarkerType
    {
        None,
        SquigglyUnderline,
        NormalUnderline,
        DottedUnderline
    }

    sealed class TextMarker : TextSegment, ITextMarker
    {
        public TextMarker(int startOffset, int length)
        {
            StartOffset = startOffset;
            Length = length;
        }

        public Color? BackgroundColor { get; set; }
        public Color? ForegroundColor { get; set; }
        public TextMarkerType MarkerType { get; set; }
        public Color? MarkerColor { get; set; }
    }

    public interface ITextMarkerService
    {
        ITextMarker Create(int startOffset, int length);
        void RemoveAll();
        IEnumerable<ITextMarker> GetMarkersAtOffset(int offset);
    }

    sealed class TextMarkerService : IBackgroundRenderer, ITextMarkerService
    {
        readonly TextSegmentCollection<TextMarker> _markers;
        readonly TextEditor _editor;

        TextMarkerService(TextEditor editor)
        {
            _editor = editor;
            _markers = new TextSegmentCollection<TextMarker>(editor.Document);
            editor.TextArea.TextView.BackgroundRenderers.Add(this);
        }

        public static ITextMarkerService Attach(TextEditor editor)
        {
            var svc = new TextMarkerService(editor);
            editor.TextArea.TextView.Services.AddService(typeof(TextMarkerService), svc);
            return svc;
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_markers == null || !_editor.IsVisible || textView.VisualLinesValid == false) return;
            var visualLines = textView.VisualLines;
            if (visualLines.Count == 0) return;

            int viewStart = visualLines[0].FirstDocumentLine.Offset;
            int viewEnd = visualLines[^1].LastDocumentLine.EndOffset;

            foreach (var m in _markers.FindOverlappingSegments(viewStart, viewEnd - viewStart))
            {
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, m))
                {
                    // 배경
                    if (m.BackgroundColor.HasValue)
                    {
                        var brush = new SolidColorBrush(m.BackgroundColor.Value) { Opacity = 0.6 };
                        drawingContext.DrawRoundedRectangle(brush, null, r, 2, 2);
                    }
                    // 밑줄
                    if (m.MarkerType != TextMarkerType.None && m.MarkerColor.HasValue)
                    {
                        var pen = new Pen(new SolidColorBrush(m.MarkerColor.Value), 1);
                        double y = r.Bottom - 1;
                        switch (m.MarkerType)
                        {
                            case TextMarkerType.NormalUnderline:
                                drawingContext.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
                                break;
                            case TextMarkerType.DottedUnderline:
                                pen.DashStyle = DashStyles.Dot;
                                drawingContext.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
                                break;
                            case TextMarkerType.SquigglyUnderline:
                                DrawSquiggly(drawingContext, pen, r.Left, r.Right, y);
                                break;
                        }
                    }
                }
            }
        }

        static void DrawSquiggly(DrawingContext dc, Pen pen, double x1, double x2, double y)
        {
            const double step = 3.5;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool up = true;
                ctx.BeginFigure(new Point(x1, y), false, false);
                for (double x = x1; x < x2; x += step)
                {
                    y += up ? -1 : 1;
                    ctx.LineTo(new Point(x + step, y), true, false);
                    up = !up;
                }
            }
            dc.DrawGeometry(null, pen, geo);
        }

        public ITextMarker Create(int startOffset, int length)
        {
            if (length <= 0) length = 1;
            var m = new TextMarker(startOffset, length);
            _markers.Add(m);
            _editor.TextArea.TextView.InvalidateLayer(Layer);
            return m;
        }

        public void RemoveAll()
        {
            _markers.Clear();
            _editor.TextArea.TextView.InvalidateLayer(Layer);
        }

        public IEnumerable<ITextMarker> GetMarkersAtOffset(int offset)
        {
            return _markers.FindSegmentsContaining(offset);
        }
    }

}