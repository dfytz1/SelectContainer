using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Grasshopper.Rhinoceros.Model;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace SelectContainer;

/// <summary>
/// Runtime-only hook: wires double-click geometry picking into native GH persistent parameters.
/// Does not subclass GH types or touch document serialization.
/// </summary>
internal static class SelectContainerCanvasHook
{
	private static readonly Dictionary<string, ObjectType> GeometryFilterByTypeName =
		new(StringComparer.OrdinalIgnoreCase)
		{
			["Geometry"] = ObjectType.AnyObject,
			["Curve"] = ObjectType.Curve,
			// GH often reports plural "Breps" for native Brep containers
			["Brep"] =
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion,
			["Breps"] =
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion,
			["SubD"] = ObjectType.SubD,
			["SubDs"] = ObjectType.SubD,
			["Mesh"] = ObjectType.Mesh,
			["Surface"] = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh,
			["Point"] = ObjectType.Point,
			["Line"] = ObjectType.Curve,
			["Plane"] = ObjectType.Surface | ObjectType.Brep | ObjectType.Curve | ObjectType.Point,
			["Vector"] = ObjectType.Curve,
			["Box"] =
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion |
				ObjectType.Mesh |
				ObjectType.SubD,
			["Boxes"] =
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion |
				ObjectType.Mesh |
				ObjectType.SubD,
			["Rectangle"] =
				ObjectType.Curve |
				ObjectType.Surface |
				ObjectType.Brep |
				ObjectType.Extrusion |
				ObjectType.Mesh,
			["Rectangles"] =
				ObjectType.Curve |
				ObjectType.Surface |
				ObjectType.Brep |
				ObjectType.Extrusion |
				ObjectType.Mesh,
			["Extrusion"] = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh,
			["Extrusions"] = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh,
			["Instance Reference"] =
				ObjectType.InstanceReference |
				ObjectType.InstanceDefinition |
				ObjectType.SubD |
				ObjectType.Mesh |
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion |
				ObjectType.Curve,
			["InstanceReference"] =
				ObjectType.InstanceReference |
				ObjectType.InstanceDefinition |
				ObjectType.SubD |
				ObjectType.Mesh |
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion |
				ObjectType.Curve,
			["Block Instance"] =
				ObjectType.InstanceReference |
				ObjectType.InstanceDefinition |
				ObjectType.SubD |
				ObjectType.Mesh |
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion |
				ObjectType.Curve,
			["Block Instances"] =
				ObjectType.InstanceReference |
				ObjectType.InstanceDefinition |
				ObjectType.SubD |
				ObjectType.Mesh |
				ObjectType.Brep |
				ObjectType.Surface |
				ObjectType.Extrusion |
				ObjectType.Curve,
			["Model Object"] = ObjectType.AnyObject,
			["ModelObject"] = ObjectType.AnyObject,
			["Model"] = ObjectType.AnyObject,
			["Circle"] = ObjectType.Curve,
			["Arc"] = ObjectType.Curve,
		};

	internal static void Register()
	{
		Instances.CanvasCreated += OnCanvasCreated;
	}

	private static void OnCanvasCreated(GH_Canvas canvas)
	{
		canvas.MouseDoubleClick += OnCanvasDoubleClick;
	}

	private static void OnCanvasDoubleClick(object sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Left)
			return;
		if (Control.ModifierKeys != Keys.None)
			return;

		var canvasControl = sender as GH_Canvas;
		if (canvasControl == null)
			return;

		var doc = canvasControl.Document;
		if (doc == null)
			return;

		PointF canvasPoint;
		try
		{
			canvasPoint = canvasControl.Viewport.UnprojectPoint(new PointF(e.Location.X, e.Location.Y));
		}
		catch
		{
			return;
		}

		IGH_DocumentObject hit = null;
		for (var i = doc.Objects.Count - 1; i >= 0; i--)
		{
			var obj = doc.Objects[i];
			var attrs = obj.Attributes;
			if (attrs == null)
				continue;

			var b = attrs.Bounds;
			if (b.IsEmpty)
				continue;

			if (b.Contains(canvasPoint))
			{
				hit = obj;
				break;
			}
		}

		if (hit == null || !GHPersistentParamKinds.IsEligibleDoubleClickContainer(hit))
			return;

		if (hit is not IGH_Param ghParam)
			return;

		var typeNameKey = ghParam.TypeName?.Trim() ?? "";
		if (string.IsNullOrEmpty(typeNameKey) ||
		    !GeometryFilterByTypeName.TryGetValue(typeNameKey, out var filter))
			return;

		try
		{
			RhinoApp.SetFocusToMainWindow();

			var go = new GetObject();
			go.SetCommandPrompt("Select geometry for " + ghParam.NickName);
			go.GeometryFilter = filter;
			go.GetMultiple(1, 0);

			if (go.CommandResult() != Result.Success)
				return;

			var goos = new List<IGH_Goo>(go.ObjectCount);
			for (var i = 0; i < go.ObjectCount; i++)
			{
				var goo = TryCreateGoo(typeNameKey, go.Object(i));
				if (goo != null)
					goos.Add(goo);
			}

			if (goos.Count == 0)
				return;

			ClearAndAppendPersistent(hit, goos);

			// Required when mutating PersistentData outside GH's own editors; otherwise previews / volatile
			// data can fall out of sync (geometry present but Rhino/GH viewport preview missing).
			hit.OnObjectChanged(GH_ObjectEventType.PersistentData);
			TryRefreshReferencedVolatileData(hit);
			hit.ExpireSolution(true);

			var ghDoc = canvasControl.Document;
			if (ghDoc != null)
				ghDoc.ScheduleSolution(15);

			canvasControl.Refresh();
		}
		catch
		{
			// Swallow selection / reflection failures so the definition is never mutated on error.
		}
	}



	private static IGH_Goo TryCreateGoo(string typeKey, ObjRef r)
	{
		if (r == null)
			return null;

		var geomBase = r.Geometry();

		if (typeKey.Equals("Geometry", StringComparison.OrdinalIgnoreCase))
			return TryNativeGeometricGoo(r, geomBase);

		switch (typeKey.ToLowerInvariant())
		{
			case "curve":
				return TryCreateCurveGoo(r, geomBase);
			case "line":
				return TryCreateLineGoo(r, geomBase);
			case "breps":
			case "brep":
				return TryCreateBrepGoo(r, geomBase);
			case "subds":
			case "subd":
				return TryCreateSubDGoo(r, geomBase);
			case "boxes":
			case "box":
				return TryCreateBoxGoo(r, geomBase);
			case "rectangles":
			case "rectangle":
				return TryCreateRectangleGoo(r, geomBase);
			case "extrusions":
			case "extrusion":
				return TryCreateExtrusionGoo(r, geomBase);
			case "instance reference":
			case "instancereference":
			case "block instance":
			case "block instances":
				return TryCreateInstanceReferenceGoo(r, geomBase);
			case "model object":
			case "modelobject":
			case "model":
				return TryCreateModelObjectGoo(r);
			case "mesh":
				return TryCreateMeshGoo(r, geomBase);
			case "surface":
				return TryCreateSurfaceGoo(r, geomBase);
			case "point":
				return TryCreatePointGoo(r, geomBase);
			case "plane":
				return TryCreatePlaneGoo(r, geomBase);
			case "vector":
				return TryCreateVectorGoo(r, geomBase);
			case "circle":
				return TryCreateCircleGoo(r, geomBase);
			case "arc":
				return TryCreateArcGoo(r, geomBase);
		}

		return TryFallBackGeometric(r, geomBase);
	}

	private static readonly MethodInfo ObjRefToGeometryWithLoad =
		typeof(GH_Convert).GetMethod(
			nameof(GH_Convert.ObjRefToGeometry),
			BindingFlags.Public | BindingFlags.Static,
			null,
			new[] { typeof(ObjRef), typeof(bool) },
			null);

	private delegate bool GhConvertTry<T>(object src, ref T target) where T : class, IGH_Goo;

	/// <summary>
	/// Referenced goos are often "invalid" until loaded; ignoring that made us fall back to
	/// duplicated geometry and broke live Rhino updates.
	/// </summary>
	private static bool ReferencedOrValid(IGH_GeometricGoo geo)
	{
		if (geo == null)
			return false;
		return geo.IsReferencedGeometry || geo.IsValid;
	}

	private static IGH_GeometricGoo TryInvokeObjRefUnloaded(ObjRef r)
	{
		if (ObjRefToGeometryWithLoad == null || r == null)
			return null;

		try
		{
			return ObjRefToGeometryWithLoad.Invoke(null, new object[] { r, false }) as IGH_GeometricGoo;
		}
		catch
		{
			return null;
		}
	}

	private static IEnumerable<object> PickConversionSources(ObjRef r, GeometryBase geomBase)
	{
		if (r == null)
			yield break;

		yield return r;

		var ro = ResolveRhinoObject(r);
		if (ro != null)
			yield return ro;

		var og = GH_Convert.ObjRefToGeometry(r);
		if (og != null)
			yield return og;

		var unloaded = TryInvokeObjRefUnloaded(r);
		if (unloaded != null && !ReferenceEquals(unloaded, og))
			yield return unloaded;

		if (geomBase != null)
			yield return geomBase;
	}

	private static bool AcceptConverted<T>(T target) where T : class
	{
		if (target == null)
			return false;

		if (target is IGH_GeometricGoo geo)
			return ReferencedOrValid(geo);

		return target is IGH_Goo g && g.IsValid;
	}

	private static T TryGhPrimarySecondary<T>(
		IEnumerable<object> sources,
		GhConvertTry<T> primary,
		GhConvertTry<T> secondary) where T : class, IGH_Goo
	{
		foreach (var src in sources)
		{
			if (src == null)
				continue;

			T target = null;
			if (primary(src, ref target) && AcceptConverted(target))
				return target;

			target = null;
			if (secondary(src, ref target) && AcceptConverted(target))
				return target;
		}

		return null;
	}

	private static IGH_Goo TryFallBackGeometric(ObjRef r, GeometryBase geomBase)
	{
		foreach (var src in PickConversionSources(r, geomBase))
		{
			var g = GH_Convert.ToGeometricGoo(src);
			if (ReferencedOrValid(g as IGH_GeometricGoo))
				return g;
		}

		if (geomBase != null)
		{
			var dg = GH_Convert.ToGeometricGoo(geomBase);
			if (ReferencedOrValid(dg as IGH_GeometricGoo))
				return dg;

			return new GH_ObjectWrapper(geomBase);
		}

		return null;
	}

	/// <summary>
	/// Mirrors GH's ObjRef-backed "Set Geometry" behaviour (referenced geometry + typed goos).
	/// </summary>
	private static IGH_Goo TryNativeGeometricGoo(ObjRef r, GeometryBase geomBase)
	{
		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateCurveGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Curve>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHCurve_Primary,
			GH_Convert.ToGHCurve_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is Curve c)
			return new GH_Curve(c);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateLineGoo(ObjRef r, GeometryBase geomBase)
	{
		var sources = PickConversionSources(r, geomBase);

		var ln = TryGhPrimarySecondary<GH_Line>(
			sources,
			GH_Convert.ToGHLine_Primary,
			GH_Convert.ToGHLine_Secondary);
		if (ln != null)
			return ln;

		var curveRef = TryGhPrimarySecondary<GH_Curve>(
			sources,
			GH_Convert.ToGHCurve_Primary,
			GH_Convert.ToGHCurve_Secondary);
		if (curveRef != null)
		{
			GH_Line fromCurveRef = null;
			if (GH_Convert.ToGHLine_Primary(curveRef, ref fromCurveRef) && AcceptConverted(fromCurveRef))
				return fromCurveRef;

			fromCurveRef = null;
			if (GH_Convert.ToGHLine_Secondary(curveRef, ref fromCurveRef) && AcceptConverted(fromCurveRef))
				return fromCurveRef;
		}

		if (geomBase is LineCurve lineCurve)
			return new GH_Line(lineCurve.Line);
		if (geomBase is Curve lc && lc.IsLinear(RhinoMath.ZeroTolerance))
			return new GH_Line(new Line(lc.PointAtStart, lc.PointAtEnd));
		if (geomBase is Curve lc2)
			return new GH_Curve(lc2);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateMeshGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Mesh>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHMesh_Primary,
			GH_Convert.ToGHMesh_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is Mesh m)
			return new GH_Mesh(m);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateSurfaceGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Surface>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHSurface_Primary,
			GH_Convert.ToGHSurface_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is Brep brep && brep.Faces.Count > 0)
		{
			var face = brep.Faces[0];
			if (face.IsSurface)
				return new GH_Surface(face.UnderlyingSurface());
		}

		if (geomBase is Surface s)
			return new GH_Surface(s);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreatePointGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Point>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHPoint_Primary,
			GH_Convert.ToGHPoint_Secondary);
		if (gh != null)
			return gh;

		var opr = r.Point();
		if (opr != null && opr.Location.IsValid)
			return new GH_Point(opr.Location);
		if (geomBase is Rhino.Geometry.Point rgPt)
			return new GH_Point(rgPt.Location);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreatePlaneGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Plane>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHPlane_Primary,
			GH_Convert.ToGHPlane_Secondary);
		if (gh != null)
			return gh;

		if (TryExtractPlane(geomBase, out var pl))
			return new GH_Plane(pl);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateVectorGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Vector>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHVector_Primary,
			GH_Convert.ToGHVector_Secondary);
		if (gh != null)
			return gh;

		if (TryExtractVector(geomBase, out var vec))
			return new GH_Vector(vec);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateCircleGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Circle>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHCircle_Primary,
			GH_Convert.ToGHCircle_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is Curve cv && cv.TryGetCircle(out var circle))
			return new GH_Circle(circle);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateArcGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Arc>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHArc_Primary,
			GH_Convert.ToGHArc_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is Curve cv2 && cv2.TryGetArc(out var arc))
			return new GH_Arc(arc);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateBrepGoo(ObjRef r, GeometryBase geomBase)
	{
		if (geomBase == null)
			return null;

		var gh = TryGhPrimarySecondary<GH_Brep>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHBrep_Primary,
			GH_Convert.ToGHBrep_Secondary);
		if (gh != null)
			return gh;

		switch (geomBase)
		{
			case Brep b when b.IsValid:
				return new GH_Brep(b);
			case Extrusion ex:
			{
				var bx = ex.ToBrep(false);
				return bx != null && bx.IsValid ? new GH_Brep(bx) : TryFallBackGeometric(r, geomBase);
			}
			case Surface s:
			{
				var br = Brep.CreateFromSurface(s);
				return br != null && br.IsValid ? new GH_Brep(br) : TryFallBackGeometric(r, geomBase);
			}
		}

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateSubDGoo(ObjRef r, GeometryBase geomBase)
	{
		if (geomBase == null)
			return null;

		var gh = TryGhPrimarySecondary<GH_SubD>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHSubD_Primary,
			GH_Convert.ToGHSubD_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is SubD sd && sd.IsValid)
		{
			GH_SubD target = null;
			if (GH_Convert.ToGHSubD_Primary(sd, ref target) && target != null && ReferencedOrValid(target))
				return target;
		}

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateBoxGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Box>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHBox_Primary,
			GH_Convert.ToGHBox_Secondary);
		if (gh != null)
			return gh;

		GH_Box boxConverted = null;
		if (geomBase != null)
		{
			if (GH_Convert.ToGHBox_Primary(geomBase, ref boxConverted) && ReferencedOrValid(boxConverted))
				return boxConverted;

			boxConverted = null;
			if (GH_Convert.ToGHBox_Secondary(geomBase, ref boxConverted) && ReferencedOrValid(boxConverted))
				return boxConverted;
		}

		if (geomBase is Extrusion ex && ex.IsValid)
		{
			var bb = ex.GetBoundingBox(true);
			if (bb.IsValid)
				return new GH_Box(new Box(bb));
		}

		if (geomBase is Brep brepBox && brepBox.IsValid)
		{
			var bb = brepBox.GetBoundingBox(true);
			if (bb.IsValid)
				return new GH_Box(new Box(bb));
		}

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateRectangleGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Rectangle>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHRectangle_Primary,
			GH_Convert.ToGHRectangle_Secondary);
		if (gh != null)
			return gh;

		GH_Rectangle converted = null;
		if (geomBase != null)
		{
			var ghCast = new GH_Rectangle();
			if (ghCast.CastFrom(geomBase) && ReferencedOrValid(ghCast))
				return ghCast;

			if (GH_Convert.ToGHRectangle_Primary(geomBase, ref converted) &&
			    ReferencedOrValid(converted))
				return converted;

			converted = null;
			if (GH_Convert.ToGHRectangle_Secondary(geomBase, ref converted) &&
			    ReferencedOrValid(converted))
				return converted;
		}

		if (geomBase is Curve c && c.IsValid)
			if (RectangleFromRectangleLikeCurve(c, out var r3FromCurve) && r3FromCurve.IsValid)
				return new GH_Rectangle(r3FromCurve);

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateExtrusionGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_Extrusion>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHExtrusion_Primary,
			GH_Convert.ToGHExtrusion_Secondary);
		if (gh != null)
			return gh;

		GH_Extrusion converted = null;
		if (geomBase != null &&
		    GH_Convert.ToGHExtrusion_Primary(geomBase, ref converted) &&
		    ReferencedOrValid(converted))
			return converted;

		converted = null;
		if (geomBase != null &&
		    GH_Convert.ToGHExtrusion_Secondary(geomBase, ref converted) &&
		    ReferencedOrValid(converted))
			return converted;

		return TryFallBackGeometric(r, geomBase);
	}

	private static IGH_Goo TryCreateInstanceReferenceGoo(ObjRef r, GeometryBase geomBase)
	{
		var gh = TryGhPrimarySecondary<GH_InstanceReference>(
			PickConversionSources(r, geomBase),
			GH_Convert.ToGHInstanceReference_Primary,
			GH_Convert.ToGHInstanceReference_Secondary);
		if (gh != null)
			return gh;

		if (geomBase is InstanceReferenceGeometry irg)
		{
			GH_InstanceReference converted = null;
			if (GH_Convert.ToGHInstanceReference_Primary(irg, ref converted) &&
			    ReferencedOrValid(converted))
				return converted;

			converted = null;
			if (GH_Convert.ToGHInstanceReference_Secondary(irg, ref converted) &&
			    ReferencedOrValid(converted))
				return converted;
		}

		return TryFallBackGeometric(r, geomBase);
	}

	/// <summary>
	/// Fits a planar rectangle curve (polyline, polyline approximation, Nurbs slab) into Rectangle3d.
	/// </summary>
	private static bool RectangleFromRectangleLikeCurve(Curve c, out Rectangle3d r3)
	{
		r3 = Rectangle3d.Unset;

		Rhino.Geometry.Polyline plFromCurve = null;
		if (c.TryGetPolyline(out plFromCurve) && plFromCurve != null && plFromCurve.Count >= 3)
		{
			if (!plFromCurve.IsClosed && c.IsClosed)
				plFromCurve.Add(plFromCurve[0]);
		}
		else
		{
			plFromCurve = ApproximateRectanglePolylineFromCurve(c);
		}

		if (plFromCurve == null || plFromCurve.Count < 4)
			return false;

		if (!plFromCurve.IsClosed)
			plFromCurve.Add(plFromCurve[0]);

		var fromPl = Rectangle3d.CreateFromPolyline(plFromCurve);
		if (!fromPl.IsValid)
			return false;

		r3 = fromPl;
		return true;
	}

	private static Rhino.Geometry.Polyline ApproximateRectanglePolylineFromCurve(Curve c)
	{
		var doc = RhinoDoc.ActiveDoc;
		var tol = doc != null ? doc.ModelAbsoluteTolerance : 0.01;
		var ang = doc != null ? doc.ModelAngleToleranceRadians : 0.1;

		try
		{
			var pc = c.ToPolyline(tol, ang, 0.01, double.MaxValue);
			if (pc == null)
				return null;

			if (pc.TryGetPolyline(out var pl) && pl != null)
				return pl;
		}
		catch
		{
			return null;
		}

		return null;
	}

	private static IGH_Goo TryCreateModelObjectGoo(ObjRef r)
	{
		var ro = ResolveRhinoObject(r);
		if (ro == null)
			return null;

		try
		{
			// Rhino 8 "Model Object" goo is Grasshopper.Rhinoceros.Model.ModelObject (not a separate GH_ModelObject type).
			return new ModelObject(ro);
		}
		catch
		{
			return null;
		}
	}

	private static RhinoObject ResolveRhinoObject(ObjRef r)
	{
		if (r == null)
			return null;

		var direct = r.Object();
		if (direct != null)
			return direct;

		var doc = RhinoDoc.ActiveDoc;
		if (doc == null)
			return null;

		var id = r.ObjectId;
		return id != Guid.Empty ? doc.Objects.FindId(id) : null;
	}

	private static bool TryExtractPlane(GeometryBase geom, out Plane plane)
	{
		plane = Plane.Unset;
		if (geom == null)
			return false;

		if (geom is PlaneSurface ps)
		{
			plane = ps.Plane;
			return plane.IsValid;
		}

		if (geom is Brep brep && brep.IsValid && brep.Faces.Count > 0)
			return brep.Faces[0].TryGetPlane(out plane);

		return false;
	}

	private static bool TryExtractVector(GeometryBase geom, out Vector3d vec)
	{
		vec = Vector3d.Zero;
		if (geom is not Curve crv || !crv.IsValid)
			return false;

		var ds = crv.PointAtStart;
		var de = crv.PointAtEnd;
		vec = de - ds;
		if (vec.Length <= RhinoMath.ZeroTolerance)
			return false;

		vec.Unitize();
		return vec.IsValid;
	}

	private static void TryRefreshReferencedVolatileData(IGH_DocumentObject obj)
	{
		if (obj == null)
			return;

		for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
		{
			var method = t.GetMethod(
				"LoadVolatileReferencedData",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				Type.EmptyTypes,
				null);
			if (method == null)
				continue;

			try
			{
				method.Invoke(obj, null);
			}
			catch
			{
				// Ignore if GH version hides or changes the helper.
			}

			break;
		}
	}

	private static void ClearAndAppendPersistent(IGH_DocumentObject obj, IList<IGH_Goo> goos)
	{
		var pdProp = obj.GetType().GetProperty(
			"PersistentData",
			BindingFlags.Public | BindingFlags.Instance);
		var persistentData = pdProp?.GetValue(obj);
		if (persistentData == null)
			return;

		var pdType = persistentData.GetType();
		var clearMethod = pdType.GetMethod(
			"Clear",
			BindingFlags.Public | BindingFlags.Instance,
			null,
			Type.EmptyTypes,
			null);
		clearMethod?.Invoke(persistentData, null);

		foreach (var goo in goos)
		{
			if (goo == null)
				continue;

			var pick = PickAppendPersistent(pdType, goo);
			if (pick == null)
				break;

			pick.Invoke(persistentData, new object[] { goo });
		}
	}

	private static MethodInfo PickAppendPersistent(Type persistentDataClrType, IGH_Goo goo)
	{
		if (goo == null)
			return null;

		var gooType = goo.GetType();

		foreach (var method in persistentDataClrType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
		{
			if (method.Name != "Append" || method.GetParameters().Length != 1)
				continue;

			var paramType = method.GetParameters()[0].ParameterType;
			if (paramType == gooType)
				return method;
		}

		MethodInfo pick = null;
		foreach (var method in persistentDataClrType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
		{
			if (method.Name != "Append" || method.GetParameters().Length != 1)
				continue;

			var paramType = method.GetParameters()[0].ParameterType;
			if (!paramType.IsInstanceOfType(goo))
				continue;

			if (pick == null)
			{
				pick = method;
				continue;
			}

			var bestType = pick.GetParameters()[0].ParameterType;
			if (paramType.IsSubclassOf(bestType))
				pick = method;
			else if (!bestType.IsSubclassOf(paramType) && !paramType.IsInterface && bestType.IsInterface)
				pick = method;
		}

		return pick;
	}

	private static class GHPersistentParamKinds
	{
		private static readonly Type PersistentParamOpenGeneric = typeof(GH_PersistentParam<>);

		/// <summary>
		/// Grasshopper persistent params are not all <c>IGH_Goo</c> (e.g. Model Object). Any
		/// <see cref="GH_PersistentParam{T}"/> should still allow double-click picking.
		/// </summary>
		internal static bool IsEligibleDoubleClickContainer(IGH_DocumentObject obj)
		{
			for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
			{
				if (!t.IsGenericType)
					continue;

				if (t.GetGenericTypeDefinition().Equals(PersistentParamOpenGeneric))
					return true;
			}

			return false;
		}
	}
}
