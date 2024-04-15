/*
Copyright (c) 2024, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ClipperLib;
using Matter_CAD_Lib.DesignTools.Interfaces;
using MatterHackers.Agg.Transform;
using MatterHackers.Agg.UI;
using MatterHackers.Agg.VertexSource;
using MatterHackers.DataConverters2D;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl;
using MatterHackers.MatterControl.DesignTools;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.PolygonMesh.Processors;
using MatterHackers.VectorMath;
using Newtonsoft.Json;
using Polygon = System.Collections.Generic.List<ClipperLib.IntPoint>;
using Polygons = System.Collections.Generic.List<System.Collections.Generic.List<ClipperLib.IntPoint>>;
using Matter_CAD_Lib.DesignTools.Obsolete;
using MatterHackers.Agg;
using MatterControlLib.DesignTools.Operations.Path;

namespace Matter_CAD_Lib.DesignTools.Objects3D
{
    public class BorderPathObject3D : OperationSourceContainerObject3D, IPropertyGridModifier, IEditorDraw, IPathProvider, IPrimaryOperationsSpecifier
    {
        public enum BaseTypes
        {
            Rectangle,
            Circle,
            /* Oval, Frame,*/
            Outline
        }

        public enum CenteringTypes
        {
            Bounds,
            Weighted
        }

        private readonly double scalingForClipper = 1000;

        public BorderPathObject3D()
        {
            Name = "Border".Localize();
        }

        public override bool CanApply => true;

        [EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Tabs)]
        public BaseTypes BaseType { get; set; } = BaseTypes.Rectangle;

        [DisplayName("Expand")]
        [Slider(0, 30, Easing.EaseType.Quadratic, snapDistance: .5)]
        public DoubleOrExpression BaseSize { get; set; } = 3;

        [Slider(0, 10, Easing.EaseType.Quadratic, snapDistance: .1)]
        public DoubleOrExpression InfillAmount { get; set; } = 3;

        [EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
        public ExpandStyles Style { get; set; } = ExpandStyles.Round;


        [EnumDisplay(Mode = EnumDisplayAttribute.PresentationMode.Buttons)]
        public CenteringTypes Centering { get; set; } = CenteringTypes.Weighted;

        public bool MeshIsSolidObject => false;

        public VertexStorage VertexStorage { get; set; } = new VertexStorage();

        public override void Cancel(UndoBuffer undoBuffer)
        {
            using (RebuildLock())
            {
                using (new CenterAndHeightMaintainer(this))
                {
                    var firstChild = Children.FirstOrDefault();

                    // only keep the first object
                    Children.Modify(list =>
                    {
                        list.Clear();
                        // add back in the sourceContainer
                        list.Add(firstChild);
                    });
                }
            }

            base.Cancel(undoBuffer);
        }

        public IVertexSource GetRawPath()
        {
            return VertexStorage;
        }

        public static async Task<BorderPathObject3D> Create()
        {
            var item = new BorderPathObject3D();
            await item.Rebuild();
            return item;
        }

        public override async void OnInvalidate(InvalidateArgs invalidateArgs)
        {
            if ((invalidateArgs.InvalidateType.HasFlag(InvalidateType.Children)
                || invalidateArgs.InvalidateType.HasFlag(InvalidateType.Matrix)
                || invalidateArgs.InvalidateType.HasFlag(InvalidateType.Path)
                || invalidateArgs.InvalidateType.HasFlag(InvalidateType.Mesh))
                && invalidateArgs.Source != this
                && !RebuildLocked)
            {
                // make sure we clear our cache
                await Rebuild();
            }
            else if (invalidateArgs.InvalidateType.HasFlag(InvalidateType.Properties) && invalidateArgs.Source == this)
            {
                await Rebuild();
            }
            else if (Expressions.NeedRebuild(this, invalidateArgs))
            {
                await Rebuild();
            }
            else
            {
                base.OnInvalidate(invalidateArgs);
            }
        }

        public override Task Rebuild()
        {
            this.DebugDepth("Rebuild");

            var rebuildLock = RebuildLock();

            return ApplicationController.Instance.Tasks.Execute(
                "Base".Localize(),
                null,
                (reporter, cancellationToken) =>
                {
                    using (new CenterAndHeightMaintainer(this, MaintainFlags.Bottom))
                    {
                        RemoveAllButSource();
                        SourceContainer.Visible = true;

                        // and create the base
                        var vertexSource = this.CombinedVisibleChildrenPaths();
                        VertexStorage = new VertexStorage(vertexSource);

                        // Convert VertexSource into expected Polygons
                        GenerateBase(VertexStorage.CreatePolygons());
                        SourceContainer.Visible = false;
                    }

                    UiThread.RunOnIdle(() =>
                    {
                        rebuildLock.Dispose();
                        Invalidate(InvalidateType.DisplayValues);
                        this.DoRebuildComplete();
                        Parent?.Invalidate(new InvalidateArgs(this, InvalidateType.Children));
                    });

                    return Task.CompletedTask;
                });
        }

        private static Polygon GetBoundingPolygon(Polygons basePolygons)
        {
            var min = new IntPoint(long.MaxValue, long.MaxValue);
            var max = new IntPoint(long.MinValue, long.MinValue);

            foreach (Polygon polygon in basePolygons)
            {
                foreach (IntPoint point in polygon)
                {
                    min.X = Math.Min(point.X - 10, min.X);
                    min.Y = Math.Min(point.Y - 10, min.Y);
                    max.X = Math.Max(point.X + 10, max.X);
                    max.Y = Math.Max(point.Y + 10, max.Y);
                }
            }

            var boundingPoly = new Polygon();
            boundingPoly.Add(min);
            boundingPoly.Add(new IntPoint(min.X, max.Y));
            boundingPoly.Add(max);
            boundingPoly.Add(new IntPoint(max.X, min.Y));

            return boundingPoly;
        }

        private Polygon GetBoundingCircle(Polygons basePolygons)
        {
            IntPoint center;
            double radius;

            if (Centering == CenteringTypes.Bounds)
            {
                IEnumerable<Vector2> GetVertices()
                {
                    foreach (var polygon in basePolygons)
                    {
                        foreach (var positon in polygon)
                        {
                            yield return new Vector2(positon.X, positon.Y);
                        }
                    }
                }

                var circle = SmallestEnclosingCircle.MakeCircle(GetVertices());

                center = new IntPoint(circle.Center.X, circle.Center.Y);
                radius = (long)circle.Radius;
            }
            else
            {
                var outsidePolygons = new Polygons();
                // remove all holes from the polygons so we only center the major outlines
                var polygons = GetRawPath().CreatePolygons();
                polygons = polygons.GetCorrectedWinding();

                foreach (var polygon in polygons)
                {
                    if (polygon.GetWindingDirection() == 1)
                    {
                        outsidePolygons.Add(polygon);
                    }
                }

                if (outsidePolygons.Count == 0)
                {
                    // add them all
                    outsidePolygons.AddRange(polygons);
                }

                IVertexSource outsideSource = outsidePolygons.CreateVertexStorage();

                var polyCenter = outsideSource.GetWeightedCenter();

                center = new IntPoint(polyCenter.X * 1000, polyCenter.Y * 1000);
                radius = 0;

                foreach (Polygon polygon in basePolygons)
                {
                    foreach (IntPoint point in polygon)
                    {
                        long length = (point - center).Length();
                        if (length > radius)
                        {
                            radius = length;
                        }
                    }
                }
            }

            var boundingCircle = new Polygon();
            int numPoints = 100;

            for (int i = 0; i < numPoints; i++)
            {
                double angle = i / 100.0 * Math.PI * 2.0;
                IntPoint newPointOnCircle = new IntPoint(Math.Cos(angle) * radius, Math.Sin(angle) * radius) + center;
                boundingCircle.Add(newPointOnCircle);
            }

            return boundingCircle;
        }

        public void GenerateBase(Polygons polygonShape)
        {
            if (polygonShape != null
                && polygonShape.Select(p => p.Count).Sum() > 3)
            {
                Polygons polysToOffset = new Polygons();

                switch (BaseType)
                {
                    case BaseTypes.Rectangle:
                        polysToOffset.Add(GetBoundingPolygon(polygonShape));
                        break;

                    case BaseTypes.Circle:
                        polysToOffset.Add(GetBoundingCircle(polygonShape));
                        break;

                    case BaseTypes.Outline:
                        polysToOffset.AddRange(polygonShape);
                        break;
                }

                if (polysToOffset.Count > 0)
                {
                    Polygons basePolygons;

                    var infillAmount = InfillAmount.Value(this);
                    var baseSize = BaseSize.Value(this);
                    var joinType = InflatePathObject3D.GetJoinType(Style);
                    if (BaseType == BaseTypes.Outline
                        && infillAmount > 0)
                    {
                        basePolygons = polysToOffset.Offset((baseSize + infillAmount) * scalingForClipper, joinType);
                        basePolygons = basePolygons.Offset(-infillAmount * scalingForClipper, joinType);
                    }
                    else
                    {
                        basePolygons = polysToOffset.Offset(baseSize * scalingForClipper, joinType);
                    }

                    basePolygons = Clipper.CleanPolygons(basePolygons, 10);

                    VertexStorage rawVectorShape = basePolygons.PolygonToPathStorage();
                    var vectorShape = new VertexSourceApplyTransform(rawVectorShape, Affine.NewScaling(1.0 / scalingForClipper));
                    VertexStorage = new VertexStorage(vectorShape);

                    Mesh = VertexStorage.Extrude(Constants.PathPolygonsHeight);
                }
                else
                {
                    // clear the mesh
                    Mesh = null;
                }
            }
        }


        public void UpdateControls(PublicPropertyChange change)
        {
            var changeSet = new Dictionary<string, bool>();
            changeSet.Clear();

            changeSet.Add(nameof(InfillAmount), BaseType == BaseTypes.Outline);
            changeSet.Add(nameof(Centering), BaseType == BaseTypes.Circle);
            changeSet.Add(nameof(Style), BaseType == BaseTypes.Outline);

            var vertexSource = GetRawPath();
            var meshSource = this.Descendants<IObject3D>().Where((i) => i.Mesh != null);

            // first turn on all the settings we want to see
            foreach (var kvp in changeSet.Where(c => c.Value))
            {
                change.SetRowVisible(kvp.Key, () => kvp.Value);
            }

            // then turn off all the settings we want to hide
            foreach (var kvp in changeSet.Where(c => !c.Value))
            {
                change.SetRowVisible(kvp.Key, () => kvp.Value);
            }
        }

        public void DrawEditor(Object3DControlsLayer layer, DrawEventArgs e)
        {
            this.DrawPath();

            SourceContainer.Children.First().DrawPath(Color.Blue.WithAlpha(20));
        }

        public AxisAlignedBoundingBox GetEditorWorldspaceAABB(Object3DControlsLayer layer)
        {
            return this.GetWorldspaceAabbOfDrawPath();
        }

        public IEnumerable<SceneOperation> GetOperations()
        {
            return PathObject3DAbstract.GetOperations(this.GetType());
        }
    }
}