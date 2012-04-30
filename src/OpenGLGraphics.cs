//
// Copyright (c) 2012 Frank A. Krueger
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenTK;
using OpenTK.Graphics.ES11;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace CrossGraphics.OpenGL
{
	public class OpenGLGraphics : IGraphics
	{
		Font _font;
		OpenGLColor _color;
		
		OpenGLShapeStore _store;
		
		IOpenGLRenderProvider _provider;

		public OpenGLGraphics (OpenGLShapeStore store, IOpenGLRenderProvider renderProvider)
		{
			if (store == null) throw new ArgumentNullException ("store");
			if (renderProvider == null) throw new ArgumentNullException ("renderProvider");

			_provider = renderProvider;
			_store = store;

			_buffers.Add (new OpenGLBuffer ());
		}

		List<OpenGLBuffer> _buffers = new List<OpenGLBuffer> ();
		int _currentBufferIndex = 0;

		const int MaxDrawCalls = 10000;
		OpenGLDrawArrayCall[] _calls = new OpenGLDrawArrayCall[MaxDrawCalls];
		int _nextDrawCallIndex = 0;

		Dictionary<OpenGLShape, OpenGLShape> _frameShapes = new Dictionary<OpenGLShape, OpenGLShape> ();
		
		public void BeginFrame ()
		{
			_frameShapes.Clear ();

			foreach (var b in _buffers) {
				b.Length = 0;
			}
			_currentBufferIndex = 0;

			_nextDrawCallIndex = 0;
		}
		
		public void EndFrame ()
		{
			if (_frameShapes.Count == 0) return;
			
			//
			// Render the texture content
			//
			var renderTexs = from s in _frameShapes.Keys
							 where !s.Rendered
							 group s by s.TextureReference.Texture;
			foreach (var kv in renderTexs) {
				var g = kv.Key.BeginRendering ();
				foreach (var s in kv) {
					s.Info.Render (g, s.TextureReference);
					s.Rendered = true;
				}
				kv.Key.EndRendering (g);
			}
			
			//
			// Draw
			//
			GL.Enable (All.Blend);
			GL.BlendFunc (All.SrcAlpha, All.OneMinusSrcAlpha);

			GL.Disable (All.Texture2D);
			GL.EnableClientState (All.VertexArray);
			GL.EnableClientState (All.TextureCoordArray);
			GL.EnableClientState (All.ColorArray);

			var boundTexture = default (OpenGLTexture);

			var boundBufferIndex = -1;

			for (var callIndex = 0; callIndex < _nextDrawCallIndex; callIndex++ ) {

				var c = _calls[callIndex];

				if (boundBufferIndex != c.BufferIndex) {
					boundBufferIndex = c.BufferIndex;
					var b = _buffers[c.BufferIndex];
					GL.VertexPointer (2, All.Float, 0, b.Positions);
					GL.TexCoordPointer (2, All.Float, 0, b.TexCoords);
					GL.ColorPointer (4, All.UnsignedByte, 0, b.Colors);
				}

				if (boundTexture != c.Texture) {
					if (boundTexture == null && c.Texture != null) {
						GL.Enable (All.Texture2D);
					}
					else if (boundTexture != null && c.Texture == null) {
						GL.Disable (All.Texture2D);
					}

					boundTexture = c.Texture;
					if (boundTexture != null) {
						boundTexture.Bind ();
					}
				}
				
				GL.DrawArrays (c.Operation, c.Offset, c.NumVertices);
			}

			GL.DisableClientState (All.VertexArray);
			GL.DisableClientState (All.TextureCoordArray);
			GL.DisableClientState (All.ColorArray);
			GL.Disable (All.Texture2D);
		}

		void EnsureRoomForVertices (int count)
		{
			var b = _buffers[_currentBufferIndex];
			if (b.Length + count >= OpenGLBuffer.MaxLength) {
				b = new OpenGLBuffer ();
				_buffers.Add (b);
				_currentBufferIndex = _buffers.Count - 1;
			}
		}

		void AddShape (OpenGLShape shape, float x, float y)
		{
			var tex = shape.TextureReference;

			if (tex != null && tex.Texture != null && _nextDrawCallIndex < MaxDrawCalls) {
				
				//
				// Remember which shapes we're drawing so that they
				// can be rendered into their textures
				//
				if (!_frameShapes.ContainsKey (shape)) {
					_frameShapes.Add (shape, shape);
				}
				
				//
				// Get a buffer with enough space
				//
				EnsureRoomForVertices (4);
				var b = _buffers[_currentBufferIndex];
				var i = b.Length;

				//
				// The position must be computed such that the point
				// ShapeOffset is aligned to x, y
				//
				var tx = x - tex.ShapeOffset.X;
				var ty = y - tex.ShapeOffset.Y;
				b.Positions[i] = new Vector2 (tx, ty);
				b.Positions[i + 1] = new Vector2 (tx, ty + tex.Height);
				b.Positions[i + 2] = new Vector2 (tx + tex.Width, ty + tex.Height);
				b.Positions[i + 3] = new Vector2 (tx + tex.Width, ty);

				//
				// TexCoords and Color are simple, you should be able to understand them ;-)
				//
				b.TexCoords[i] = new Vector2 (tex.U, tex.V);
				b.TexCoords[i + 1] = new Vector2 (tex.U, tex.V + tex.TexHeight);
				b.TexCoords[i + 2] = new Vector2 (tex.U + tex.TexWidth, tex.V + tex.TexHeight);
				b.TexCoords[i + 3] = new Vector2 (tex.U + tex.TexWidth, tex.V);

				b.Colors[i] = _color;
				b.Colors[i + 1] = _color;
				b.Colors[i + 2] = _color;
				b.Colors[i + 3] = _color;

				b.Length += 4;

				//
				// Record the draw call
				//
				var call = new OpenGLDrawArrayCall {
					Operation = All.TriangleFan,
					BufferIndex = (byte)_currentBufferIndex,
					Offset = (ushort)i,
					NumVertices = 4,
					Texture = tex.Texture,
				};
				_calls[_nextDrawCallIndex] = call;
				_nextDrawCallIndex++;
			}
		}

		void AddSolidRect (float x, float y, float width, float height)
		{
			if (_nextDrawCallIndex < MaxDrawCalls) {

				EnsureRoomForVertices (4);
				
				var b = _buffers[_currentBufferIndex];
				var i = b.Length;

				b.Positions[i] = new Vector2 (x, y);
				b.Positions[i + 1] = new Vector2 (x, y + height);
				b.Positions[i + 2] = new Vector2 (x + width, y + height);
				b.Positions[i + 3] = new Vector2 (x + width, y);

				b.Colors[i] = _color;
				b.Colors[i + 1] = _color;
				b.Colors[i + 2] = _color;
				b.Colors[i + 3] = _color;

				b.Length += 4;
			
				var call = new OpenGLDrawArrayCall {
					Operation = All.TriangleFan,
					BufferIndex = (byte)_currentBufferIndex,
					Offset = (ushort)i,
					NumVertices = 4,
					Texture = null,
				};

				_calls[_nextDrawCallIndex] = call;
				_nextDrawCallIndex++;
			}
		}
				
		#region IGraphics implementation
		
		public void BeginEntity (object entity)
		{
		}

		public void SetFont (Font f)
		{
			_font = f;
		}

		public void SetColor (Color c)
		{
			if (c == null) {
				c = Colors.White;
			}
			_color = new OpenGLColor {
				R = (byte)c.Red,
				G = (byte)c.Green,
				B = (byte)c.Blue,
				A = (byte)c.Alpha,
			};
		}

		public void FillPolygon (Polygon poly)
		{
			if (poly.Points.Count < 1) return;
			var p0 = poly.Points[0];
			var r = OpenGLShapeInfo.Polygon (poly);
			AddShape (_store.GetShape (ref r), p0.X, p0.Y);
		}

		public void DrawPolygon (Polygon poly, float w)
		{
			if (poly.Points.Count < 1) return;
			var p0 = poly.Points[0];
			var r = OpenGLShapeInfo.Polygon (poly, w);
			AddShape (_store.GetShape (ref r), p0.X, p0.Y);
		}

		public void FillRect (float x, float y, float width, float height)
		{
			AddSolidRect (x, y, width, height);
		}

		public void DrawRect (float x, float y, float width, float height, float w)
		{
			var w2 = w / 2;
			AddSolidRect (x + w2, y - w2, width - w, w); // top
			AddSolidRect (x - w2, y - w2, w, height + w); //left
			AddSolidRect (x + width - w2, y - w2, w, height + w); //right
			AddSolidRect (x + w2, y + height - w2, width - w, w); //bottom
		}

		public void FillRoundedRect (float x, float y, float width, float height, float radius)
		{
			var r = OpenGLShapeInfo.RoundedRect (width, height, radius);
			AddShape (_store.GetShape (ref r), x, y);
		}

		public void DrawRoundedRect (float x, float y, float width, float height, float radius, float w)
		{
			var r = OpenGLShapeInfo.RoundedRect (width, height, radius, w);
			AddShape (_store.GetShape (ref r), x, y);
		}

		public void FillOval (float x, float y, float width, float height)
		{
			var r = OpenGLShapeInfo.Oval (width, height);
			AddShape (_store.GetShape (ref r), x, y);
		}

		public void DrawOval (float x, float y, float width, float height, float w)
		{
			var r = OpenGLShapeInfo.Oval (width, height, w);
			AddShape (_store.GetShape (ref r), x, y);
		}

		public void BeginLines (bool rounded)
		{
		}

		public void DrawLine (float sx, float sy, float ex, float ey, float w)
		{
			var dx = ex - sx;
			var dy = ey - sy;

			if (Math.Abs (dx) < 0.01) {
				AddSolidRect (sx - w / 2, sy, w, dy);
			}
			else if (Math.Abs (dy) < 0.01) {
				AddSolidRect (sx, sy - w / 2, dx, w);
			}
			else {
				if (dx >= 0) {
					var r = OpenGLShapeInfo.Line (dx, dy, w);
					AddShape (_store.GetShape (ref r), sx, sy);
				}
				else {
					var r = OpenGLShapeInfo.Line (-dx, -dy, w);
					AddShape (_store.GetShape (ref r), ex, ey);
				}
			}
		}

		public void EndLines ()
		{
		}

		public void DrawArc (float cx, float cy, float radius, float startAngle, float endAngle, float w)
		{
			var r = OpenGLShapeInfo.Arc (radius, startAngle, endAngle, w);
			AddShape (_store.GetShape (ref r), cx, cy);
		}

		public void DrawImage (IImage img, float x, float y, float width, float height)
		{
		}

		public void DrawString (string s, float x, float y, float width, float height, LineBreakMode lineBreak, TextAlignment align)
		{
			if (string.IsNullOrWhiteSpace (s)) return;

			if (align == TextAlignment.Left || align == TextAlignment.Justified) {
				DrawString (s, x, y);
			}
			else if (align == TextAlignment.Right) {
				var stringWidth = GetFontMetrics ().StringWidth (s);
				DrawString (s, x + width - stringWidth, y);
			}
			else {
				var stringWidth = GetFontMetrics ().StringWidth (s);
				DrawString (s, x + (width - stringWidth) / 2, y);
			}
		}

		public void DrawString (string s, float x, float y)
		{
			if (string.IsNullOrWhiteSpace (s)) return;

			var fm = GetFontMetrics ();
			var width = fm.StringWidth (s);
			var height = fm.Height;

			var xx = x;
			for (var i = 0; i < s.Length; i++) {
				var r = OpenGLShapeInfo.Character (s[i], _font, width, height);
				AddShape (_store.GetShape (ref r), xx, y);
				xx += fm.StringWidth (s, i, 1);
			}
		}

		public void SaveState ()
		{
		}

		public void SetClippingRect (float x, float y, float width, float height)
		{
		}

		public void Translate (float dx, float dy)
		{
		}

		public void Scale (float sx, float sy)
		{
		}

		public void RestoreState ()
		{
		}

		public IFontMetrics GetFontMetrics ()
		{
			return _provider.GetFontMetrics (_font);
		}

		public IImage ImageFromFile (string path)
		{
			return _provider.ImageFromFile (path);
		}
		
		#endregion
	}
			
	public enum OpenGLShapeType : int
	{
		Line = 0,
		Rect = 1,
		RoundedRect = 2,
		Oval = 3,
		Character = 4,
		Polygon = 5,
		Arc = 6,
	}
	
	public struct OpenGLShapeInfo
	{
		public OpenGLShapeType ShapeType;
		public float A, B, C, D;
		public bool Fill;
		public char Char;
		public Font Font;
		public Polygon Poly;

		public static OpenGLShapeInfo ReadXml (System.Xml.XmlReader r)
		{
			var icult = System.Globalization.CultureInfo.InvariantCulture;

			var i = new OpenGLShapeInfo ();

			while (r.Read ()) {
				if (r.IsStartElement ("Info")) {
					i.ShapeType = (OpenGLShapeType)Enum.Parse (typeof (OpenGLShapeType), r.GetAttribute ("ShapeType"));
					i.A = float.Parse (r.GetAttribute ("A"), icult);
					i.B = float.Parse (r.GetAttribute ("B"), icult);
					i.C = float.Parse (r.GetAttribute ("C"), icult);
					i.D = float.Parse (r.GetAttribute ("D"), icult);
					i.Fill = r.GetAttribute ("Fill") == "true";
					var ch = r.GetAttribute ("Char");
					i.Char = string.IsNullOrEmpty (ch) ? (char)0 : ch[0];
					var ff = r.GetAttribute ("FontFamily");
					if (!string.IsNullOrWhiteSpace (ff)) {
						var fw = r.GetAttribute ("FontWeight");
						var fo = fw == "bold" ? FontOptions.Bold : FontOptions.None;
						var fs = int.Parse (r.GetAttribute ("FontSize"), icult);
						i.Font = new Font (ff, fo, fs);
					}
				}
				else if (r.IsStartElement ("Polygon")) {
					var parts = r.GetAttribute ("Points").Split (WS, StringSplitOptions.RemoveEmptyEntries);
					var poly = new Polygon ();
					for (var j = 0; j < parts.Length; j += 2) {
						var p = new PointF (float.Parse (parts[j], icult), float.Parse (parts[j + 1], icult));
						poly.AddPoint (p);
					}
					i.Poly = poly;
				}
			}

			return i;
		}

		static readonly char[] WS = new[] { ' ', '\t', '\r', '\n' };

		public void WriteXml (System.Xml.XmlWriter w)
		{
			var icult = System.Globalization.CultureInfo.InvariantCulture;

			w.WriteStartElement ("Info");
			w.WriteAttributeString ("ShapeType", ShapeType.ToString ());
			w.WriteAttributeString ("A", A.ToString (icult));
			w.WriteAttributeString ("B", B.ToString (icult));
			w.WriteAttributeString ("C", C.ToString (icult));
			w.WriteAttributeString ("D", D.ToString (icult));
			w.WriteAttributeString ("Fill", Fill ? "true" : "false");
			if ((int)Char != 0) {
				w.WriteAttributeString ("Text", Char.ToString ());
			}
			if (Font != null) {
				w.WriteAttributeString ("FontFamily", Font.FontFamily);
				w.WriteAttributeString ("FontWeight", Font.IsBold ? "bold" : "normal");
				w.WriteAttributeString ("FontSize", Font.Size.ToString (icult));
			}
			if (Poly != null) {
				w.WriteStartElement ("Polygon");
				var pointsValue = new System.Text.StringBuilder ();
				foreach (var p in Poly.Points) {
					pointsValue.AppendFormat (icult, "{0} {1} ", p.X, p.Y);
				}
				w.WriteAttributeString ("Points", pointsValue.ToString ());
				w.WriteEndElement ();
			}
			w.WriteEndElement ();
		}

		public override string ToString ()
		{
			return string.Format ("[{0} {1} {2} {3} {4} {5}]", ShapeType, A, B, C, D, Fill);
		}

		public void Render (IGraphics g, OpenGLTextureReference tex)
		{
			g.SetColor (Colors.White);

			var x = tex.X + tex.ShapeOffset.X;
			var y = tex.Y + tex.ShapeOffset.Y;

			switch (ShapeType) {
			case OpenGLShapeType.Line:
				g.DrawLine (x, y, x + A, y + B, C);
				break;
			case OpenGLShapeType.Rect:
				if (Fill) {
					g.FillRect (x, y, A, B);
				}
				else {
					g.DrawRect (x, y, A, B, C);
				}
				break;
			case OpenGLShapeType.RoundedRect:
				if (Fill) {
					g.FillRoundedRect (x, y, A, B, C);
				}
				else {
					g.DrawRoundedRect (x, y, A, B, C, D);
				}
				break;
			case OpenGLShapeType.Oval:
				if (Fill) {
					g.FillOval (x, y, A, B);
				}
				else {
					g.DrawOval (x, y, A, B, C);
				}
				break;
			case OpenGLShapeType.Character:
				g.SetFont (Font);
				g.DrawString (Char.ToString (), x, y);
				break;
			case OpenGLShapeType.Polygon: {
					var dx = x - Poly.Points[0].X;
					var dy = y - Poly.Points[0].Y;
					var dpoly = new Polygon ();
					for (var i = 0; i < Poly.Points.Count; i++) {
						dpoly.AddPoint (Poly.Points[i].X + dx, Poly.Points[i].Y + dy);
					}
					if (Fill) {
						g.FillPolygon (dpoly);
					}
					else {
						g.DrawPolygon (dpoly, A);
					}
				}
				break;
			case OpenGLShapeType.Arc:
				g.DrawArc (x, y, A, B, C, D);
				break;
			default:
				throw new NotSupportedException ();
			}
		}
		
		public OpenGLTextureReference GetTextureRequirements ()
		{
			switch (ShapeType) {
			case OpenGLShapeType.Line: {
					var p = C;
					return new OpenGLTextureReference (A + 2 * p, Math.Abs (B) + 2 * p) {
						ShapeOffset = B >= 0 ? new Vector2 (p, p) : new Vector2 (p, p - B),
					};
				}
			case OpenGLShapeType.Rect:
				if (Fill) {
					return new OpenGLTextureReference (A, B);
				}
				else {
					var p = C;
					return new OpenGLTextureReference (A + 2 * p, B + 2 * p) {
						ShapeOffset = new Vector2 (p, p),
					};
				}
			case OpenGLShapeType.RoundedRect:
				if (Fill) {
					return new OpenGLTextureReference (A, B);
				}
				else {
					var p = D;
					return new OpenGLTextureReference (A + 2 * p, B + 2 * p) {
						ShapeOffset = new Vector2 (p, p),
					};
				}
			case OpenGLShapeType.Oval:
				if (Fill) {
					return new OpenGLTextureReference (A, B);
				}
				else {
					var p = C;
					return new OpenGLTextureReference (A + 2 * p, B + 2 * p) {
						ShapeOffset = new Vector2 (p, p),
					};
				}
			case OpenGLShapeType.Character: {
					var p = 3;
					return new OpenGLTextureReference (A + 2 * p, B + 2 * p) {
						ShapeOffset = new Vector2 (p, p),
					};
				}
			case OpenGLShapeType.Polygon: {
					var p = Fill ? 3 : A;
					var left = 0.0f;
					var top = 0.0f;
					var right = 0.0f;
					var bottom = 0.0f;
					var p0 = Poly.Points[0];
					for (var i = 1; i < Poly.Points.Count; i++) {
						var dx = Poly.Points[i].X - p0.X;
						var dy = Poly.Points[i].Y - p0.Y;
						if (dx > 0) {
							right = Math.Max (right, dx);
						}
						else if (dx < 0) {
							left = Math.Max (left, -dx);
						}
						if (dy > 0) {
							bottom = Math.Max (bottom, dy);
						}
						else if (dy < 0) {
							top = Math.Max (top, -dy);
						}
					}
					return new OpenGLTextureReference (left + right + 2 * p, top + bottom + 2 * p) {
						ShapeOffset = new Vector2 (left + p, top + p),
					};
				}
			case OpenGLShapeType.Arc: {
					var p = D;
					return new OpenGLTextureReference (2 * A + 2 * p, 2 * A + 2 * p) {
						ShapeOffset = new Vector2 (A + p, A + p),
					};
				}
			default:
				throw new NotSupportedException ();
			}
		}
		
		const float Tolerance = 0.01f;
		const float ArcAngleTolerance = (float)((2 * Math.PI) / 16);
		
		public bool Equals (ref OpenGLShapeInfo other)
		{
			if (ShapeType != other.ShapeType) return false;
			
			switch (ShapeType) {
			case OpenGLShapeType.Line:
				return Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance && Math.Abs (C - other.C) < Tolerance;
			case OpenGLShapeType.Rect:
				if (Fill) {
					return other.Fill && Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance;
				}
				else {
					return !other.Fill && Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance && Math.Abs (C - other.C) < Tolerance;
				}
			case OpenGLShapeType.RoundedRect:
				if (Fill) {
					return other.Fill && Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance && Math.Abs (C - other.C) < Tolerance;
				}
				else {
					return !other.Fill && Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance && Math.Abs (C - other.C) < Tolerance && Math.Abs (D - other.D) < Tolerance;
				}
			case OpenGLShapeType.Oval:
				if (Fill) {
					return other.Fill && Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance;
				}
				else {
					return !other.Fill && Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < Tolerance && Math.Abs (C - other.C) < Tolerance;
				}
			case OpenGLShapeType.Character:
				return Char == other.Char && Font.Size == other.Font.Size && Font.FontFamily == other.Font.FontFamily;
			case OpenGLShapeType.Polygon:
				if (Fill && !other.Fill) return false;
				if (!Fill && Math.Abs (A - other.A) >= Tolerance) return false;
				if (Poly.Points.Count != other.Poly.Points.Count) return false;
				for (var i = 1; i < Poly.Points.Count; i++) {
					var dx = Poly.Points[i].X - Poly.Points[i - 1].X;
					var odx = other.Poly.Points[i].X - other.Poly.Points[i - 1].X;
					if (Math.Abs (dx - odx) >= Tolerance) return false;
					var dy = Poly.Points[i].Y - Poly.Points[i - 1].Y;                    
					var ody = other.Poly.Points[i].Y - other.Poly.Points[i - 1].Y;
					if (Math.Abs (dy - ody) >= Tolerance) return false;
				}
				return true;
			case OpenGLShapeType.Arc:
				return Math.Abs (A - other.A) < Tolerance && Math.Abs (B - other.B) < ArcAngleTolerance && Math.Abs (C - other.C) < ArcAngleTolerance && Math.Abs (D - other.D) < Tolerance;
			default:
				throw new NotImplementedException ();
			}
		}
		
		public static OpenGLShapeInfo Line (float dx, float dy, float w)
		{
			Debug.Assert (dx >= 0, "Lines must have positive dX");
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Line,
				A = dx,
				B = dy,
				C = w,
				Fill = false,
			};
		}
		
		public static OpenGLShapeInfo Rect (float width, float height)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Rect,
				A = width,
				B = height,
				Fill = true,
			};
		}
		
		public static OpenGLShapeInfo Rect (float width, float height, float w)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Rect,
				A = width,
				B = height,
				C = w,
				Fill = false,
			};
		}
		
		public static OpenGLShapeInfo RoundedRect (float width, float height, float radius)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.RoundedRect,
				A = width,
				B = height,
				C = radius,
				Fill = true,
			};
		}
		
		public static OpenGLShapeInfo RoundedRect (float width, float height, float radius, float w)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.RoundedRect,
				A = width,
				B = height,
				C = radius,
				D = w,
				Fill = false,
			};
		}
		
		public static OpenGLShapeInfo Oval (float width, float height)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Oval,
				A = width,
				B = height,
				Fill = true,
			};
		}
		
		public static OpenGLShapeInfo Oval (float width, float height, float w)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Oval,
				A = width,
				B = height,
				C = w,
				Fill = false,
			};
		}

		public static OpenGLShapeInfo Character (char text, Font font, float width, float height)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Character,
				A = width,
				B = height,
				Char = text,
				Font = font,
			};
		}

		public static OpenGLShapeInfo Polygon (Polygon poly, float w)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Polygon,
				A = w,
				Poly = poly,
			};
		}

		public static OpenGLShapeInfo Polygon (Polygon poly)
		{
			var info = Polygon (poly, 0);
			info.Fill = true;
			return info;
		}

		public static OpenGLShapeInfo Arc (float radius, float startAngle, float endAngle, float w)
		{
			return new OpenGLShapeInfo {
				ShapeType = OpenGLShapeType.Arc,
				A = radius,
				B = startAngle,
				C = endAngle,
				D = w,
			};
		}
	}

	[System.Runtime.InteropServices.StructLayout (System.Runtime.InteropServices.LayoutKind.Sequential)]
	struct OpenGLDrawArrayCall
	{
		public byte BufferIndex;
		public byte NumVertices;
		public ushort Offset;
		public OpenGLTexture Texture;
		public All Operation;
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	struct OpenGLColor
	{
		public byte R;
		public byte G;
		public byte B;
		public byte A;
	}

	class OpenGLBuffer
	{
		public const int MaxLength = 65536 / 8;

		public Vector2[] Positions;
		public Vector2[] TexCoords;
		public OpenGLColor[] Colors;
		public int Length;

		public OpenGLBuffer ()
		{
			Positions = new Vector2[MaxLength];
			TexCoords = new Vector2[MaxLength];
			Colors = new OpenGLColor[MaxLength];
			Length = 0;
		}
	}
	
	public class OpenGLShape
	{
		public readonly OpenGLShapeInfo Info;
		public OpenGLTextureReference TextureReference;
		public bool Rendered;
		
		public OpenGLShape (OpenGLShapeInfo info)
		{
			Info = info;
		}

		public OpenGLShape (System.Xml.XmlReader r, List<OpenGLTexture> textures)
		{
			while (r.Read ()) {
				if (r.IsStartElement ("Info")) {
					Info = OpenGLShapeInfo.ReadXml (r.ReadSubtree ());
				}
				else if (r.IsStartElement ("Texture")) {
					TextureReference = new OpenGLTextureReference (r.ReadSubtree (), textures);
				}
			}

			Rendered = true;
		}

		public void WriteXml (System.Xml.XmlWriter w, List<OpenGLTexture> textures)
		{
			w.WriteStartElement ("Shape");

			Info.WriteXml (w);
			TextureReference.WriteXml (w, textures);
			
			w.WriteEndElement ();
		}
	}
	
	public abstract class OpenGLTexture : IDisposable
	{
		public uint Id;
		bool _valid;

		public int Width { get; protected set; }
		public int Height { get; protected set; }

		public bool NeedsSave;
		
		public OpenGLTexture (int width, int height)
		{
			Width = width;
			Height = height;
		}

		~OpenGLTexture ()
		{
			Dispose (false);
		}

		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		public virtual void Dispose (bool disposing)
		{
			if (Id > 0) {
				GL.DeleteTextures (1, ref Id);
				Id = 0;
			}
		}

		public abstract IGraphics BeginRendering ();

		public virtual void EndRendering (IGraphics g)
		{
			_valid = false;
		}

		protected abstract void CallTexImage2D ();
		protected void TexImage2D (IntPtr data)
		{
			GL.TexImage2D (All.Texture2D, 0, (int)All.Rgba, Width, Height, 0, All.Rgba, All.UnsignedByte, data);
		}

		public void Bind ()
		{
			if (Id == 0 || !_valid) {
				if (Id == 0) {
					GL.GenTextures (1, ref Id);
				}
				GL.BindTexture (All.Texture2D, Id);
				GL.TexParameter (All.Texture2D, All.TextureMinFilter, (int)All.Linear);
				GL.TexParameter (All.Texture2D, All.TextureMagFilter, (int)All.Linear);
				GL.TexParameter (All.Texture2D, All.TextureWrapS, (int)All.ClampToEdge);
				GL.TexParameter (All.Texture2D, All.TextureWrapT, (int)All.ClampToEdge);
				CallTexImage2D ();
				_valid = true;
			}
			else {
				GL.BindTexture (All.Texture2D, Id);
			}
		}

		public void ReleaseMemory ()
		{
			if (Id != 0) {
				GL.DeleteTextures (1, ref Id);
				Id = 0;
			}
		}

		public void AddReference (OpenGLTextureReference texRef)
		{
			_references.Add (texRef);
		}

		List<OpenGLTextureReference> _references = new List<OpenGLTextureReference> ();

		const float Pad = 2;
		
		public bool TryInsert (OpenGLTextureReference newRef)
		{
			//
			// Go column by column until we fit in a row
			//
			var x = Pad;
			while (x + newRef.Frame.Width + Pad < Width) {

				newRef.X = x;

				var columnFrame = new RectangleF (x, 0, newRef.Frame.Width, Height);
				var isects = GetReferencesIntersecting (columnFrame);

				var y = Pad;

				while (y + newRef.Frame.Height + Pad < Height) {

					newRef.Y = y;

					var intersects = false;

					for (var i = 0; i < isects.Count && !intersects; i++) {
						intersects = isects[i].IntersectsWith (newRef);
						if (intersects) {
							y = isects[i].Frame.Bottom + Pad;
						}
					}

					if (!intersects) {
						_references.Add (newRef);
						NeedsSave = true;
						newRef.Texture = this;
						newRef.U = newRef.X / Width;
						newRef.V = newRef.Y / Height;
						newRef.TexWidth = newRef.Width / Width;
						newRef.TexHeight = newRef.Height / Height;
						return true;
					}
				}

				x = isects.Average (r => r.Frame.Right) + 3;
			}

			return false;
		}

		List<OpenGLTextureReference> GetReferencesIntersecting (RectangleF frame)
		{
			var q = from r in _references
					where r.Frame.IntersectsWith (frame)
					orderby r.Frame.Y
					select r;
			return q.ToList ();
		}
	}
	
	public class OpenGLTextureReference
	{
		public OpenGLTexture Texture;

		public float X, Y, Width, Height;
		public RectangleF Frame { get { return new RectangleF (X, Y, Width, Height); } }
		
		public Vector2 ShapeOffset;

		public float U, V, TexWidth, TexHeight;
		
		public OpenGLTextureReference (float width, float height)
		{
			Width = width;
			Height = height;
		}

		public OpenGLTextureReference (System.Xml.XmlReader r, List<OpenGLTexture> textures)
		{
			var icult = System.Globalization.CultureInfo.InvariantCulture;

			while (r.Read ()) {
				if (r.IsStartElement ("Texture")) {
					var ti = int.Parse (r.GetAttribute ("Id"), icult);
					if (ti >= 0 && ti < textures.Count) {
						Texture = textures[ti];
						Texture.AddReference (this);
					}
					else {
						Texture = null;
						return;
					}

					X = float.Parse (r.GetAttribute ("X"), icult);
					Y = float.Parse (r.GetAttribute ("Y"), icult);
					Width = float.Parse (r.GetAttribute ("Width"), icult);
					Height = float.Parse (r.GetAttribute ("Height"), icult);

					U = float.Parse (r.GetAttribute ("U"), icult);
					V = float.Parse (r.GetAttribute ("V"), icult);
					TexWidth = float.Parse (r.GetAttribute ("TexWidth"), icult);
					TexHeight = float.Parse (r.GetAttribute ("TexHeight"), icult);

					var soX = float.Parse (r.GetAttribute ("OffsetX"), icult);
					var soY = float.Parse (r.GetAttribute ("OffsetY"), icult);
					ShapeOffset = new Vector2 (soX, soY);
				}
			}
		}

		public void WriteXml (System.Xml.XmlWriter w, List<OpenGLTexture> textures)
		{
			var icult = System.Globalization.CultureInfo.InvariantCulture;

			w.WriteStartElement ("Texture");
			w.WriteAttributeString ("Id", textures.IndexOf (Texture).ToString (icult));
			w.WriteAttributeString ("X", X.ToString (icult));
			w.WriteAttributeString ("Y", Y.ToString (icult));
			w.WriteAttributeString ("Width", Width.ToString (icult));
			w.WriteAttributeString ("Height", Height.ToString (icult));
			w.WriteAttributeString ("U", U.ToString (icult));
			w.WriteAttributeString ("V", V.ToString (icult));
			w.WriteAttributeString ("TexWidth", TexWidth.ToString (icult));
			w.WriteAttributeString ("TexHeight", TexHeight.ToString (icult));
			w.WriteAttributeString ("OffsetX", ShapeOffset.X.ToString (icult));
			w.WriteAttributeString ("OffsetY", ShapeOffset.Y.ToString (icult));
			w.WriteEndElement ();
		}

		public bool IntersectsWith (OpenGLTextureReference other)
		{
			return Frame.IntersectsWith (other.Frame);
		}

		public override string ToString ()
		{
			return string.Format ("[({0}, {1}, {2}, {3}) @ ({4}, {5})]", X, Y, Width, Height, ShapeOffset.X, ShapeOffset.Y);
		}
	}
	
	public abstract class OpenGLShapeStore
	{
		List<OpenGLTexture> _textures;
		List<OpenGLShape>[] _shapesByType;

		bool _needsSave;
		
		public OpenGLShapeStore ()
		{
			_textures = new List<OpenGLTexture> ();
			_shapesByType = CreateShapesByType ();
		}

		protected abstract OpenGLTexture CreateTexture (int width, int height);
		protected virtual string ShapeStorePath { get { return string.Empty; } }
		protected virtual int MaxTextureSize { get { return 2048; } }
		protected virtual OpenGLTexture ReadTexture (int id) { return null; }
		protected virtual void WriteTexture (int id, OpenGLTexture texture) { throw new NotSupportedException (); }

		static List<OpenGLShape>[] CreateShapesByType ()
		{
			var numShapeTypes = 7;
			var shapesByType = new List<OpenGLShape>[numShapeTypes];
			for (var i = 0; i < numShapeTypes; i++) {
				shapesByType[i] = new List<OpenGLShape> ();
			}
			return shapesByType;
		}

		static void EnsurePathExists (string path)
		{
			System.IO.Directory.CreateDirectory (path);
		}

		public void Open ()
		{
			var path = ShapeStorePath;
			if (string.IsNullOrEmpty (path)) return;

			EnsurePathExists (path);

			//
			// Load textures
			//
			var ti = 0;
			var foundTexture = true;
			var textures = new List<OpenGLTexture> ();
			while (foundTexture) {
				try {
					var t = ReadTexture (ti);
					foundTexture = t != null;
					if (foundTexture) {
						textures.Insert (ti, t);
					}
					ti++;
				}
				catch (Exception) {
					foundTexture = false;
				}
			}

			//
			// Load the shapes
			//
			var shapesByType = CreateShapesByType ();
			using (var r = System.Xml.XmlReader.Create (System.IO.Path.Combine (path, "ShapeStore.xml"))) {
				while (r.Read ()) {
					if (r.IsStartElement ("Shape")) {
						try {
							var s = new OpenGLShape (r.ReadSubtree (), textures);
							if (s.TextureReference.Texture != null) {
								shapesByType[(int)s.Info.ShapeType].Add (s);
							}
						}
						catch (Exception) {
						}
					}
				}
			}

			//
			// Done!
			//
			_textures = textures;
			_shapesByType = shapesByType;
		}

		public void Save ()
		{
			var path = ShapeStorePath;
			if (string.IsNullOrEmpty (path)) return;

			EnsurePathExists (path);

			//
			// Save the textures
			//
			for (var i = 0; i < _textures.Count; i++) {
				var t = _textures[i];
				if (t.NeedsSave) {
					WriteTexture (i, t);
					t.NeedsSave = false;
				}
			}

			//
			// Save the shapes
			//
			if (_needsSave) {
				var settings = new System.Xml.XmlWriterSettings {
					Indent = true,
					Encoding = System.Text.Encoding.UTF8,
				};
				using (var w = System.Xml.XmlWriter.Create (System.IO.Path.Combine (path, "ShapeStore.xml"), settings)) {
					w.WriteStartElement ("ShapeStore");
					foreach (var ss in _shapesByType) {
						foreach (var s in ss) {
							s.WriteXml (w, _textures);
						}
					}
					w.WriteEndElement ();
				}
				_needsSave = false;
			}			
		}
		
		public OpenGLShape GetShape (ref OpenGLShapeInfo info)
		{
			//
			// Hopefully, we already have this shape
			//
			var ss = _shapesByType [(int)info.ShapeType];
			foreach (var s in ss) {
				if (s.Info.Equals (ref info)) {
					return s;
				}
			}
			
			//
			// Uh oh, have to make it.
			//
			_needsSave = true;
			var shape = new OpenGLShape (info);
			ss.Add (shape);
			
			//
			// Find out its texture requirements
			//
			var r = shape.Info.GetTextureRequirements ();
			
			//
			// Try to find a texture that can meet those requirements
			//
			foreach (var t in _textures) {
				if (t.TryInsert (r)) {
					break;
				}
			}
			
			//
			// If we couldn't find a texture that could take this reference,
			// we better get another texture
			//
			if (r.Texture == null) {
				//
				// Discover the largest texture we can handle.
				// It is the smaller of that supported by OpenGL
				// and whatever the renderer can handle.
				//
				var maxSize = 0;
				GL.GetInteger (All.MaxTextureSize, ref maxSize);
				maxSize = Math.Min (MaxTextureSize, maxSize);

				//
				// Create that texture if this shape will fit in it
				//
				if (r.Frame.Width <= maxSize && r.Frame.Height <= maxSize) {
					var tex = CreateTexture (maxSize, maxSize);

					tex.NeedsSave = true;
					_textures.Add (tex);

					tex.TryInsert (r);
				}
			}
			
			shape.TextureReference = r;
			
			return shape;
		}

		public void ReleaseTextureMemory ()
		{
			foreach (var t in _textures) {
				t.ReleaseMemory ();
			}
		}
	}

	public interface IOpenGLRenderProvider
	{
		IFontMetrics GetFontMetrics (Font font);
		IImage ImageFromFile (string path);
	}	
}
