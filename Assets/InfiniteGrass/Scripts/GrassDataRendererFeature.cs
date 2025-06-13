// Upgrade‑safe version for URP 17.1+ (Render Graph only)
// Place this file in Assets/InfiniteGrass/Scripts/

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class GrassDataRendererFeature : ScriptableRendererFeature
{
    private static readonly int GrassColorRT = Shader.PropertyToID("_GrassColorRT");
    private static readonly int GrassSlopeRT = Shader.PropertyToID("_GrassSlopeRT");
    private static readonly int VpMatrix = Shader.PropertyToID("_VPMatrix");
    private static readonly int FullDensityDistance = Shader.PropertyToID("_FullDensityDistance");
    private static readonly int DensityFalloffExponent = Shader.PropertyToID("_DensityFalloffExponent");
    private static readonly int BoundsMin = Shader.PropertyToID("_BoundsMin");
    private static readonly int BoundsMax = Shader.PropertyToID("_BoundsMax");
    private static readonly int CameraPosition = Shader.PropertyToID("_CameraPosition");
    private static readonly int CenterPos = Shader.PropertyToID("_CenterPos");
    private static readonly int DrawDistance = Shader.PropertyToID("_DrawDistance");
    private static readonly int TextureUpdateThreshold = Shader.PropertyToID("_TextureUpdateThreshold");
    private static readonly int Spacing = Shader.PropertyToID("_Spacing");
    private static readonly int GridStartIndex = Shader.PropertyToID("_GridStartIndex");
    private static readonly int GridSize = Shader.PropertyToID("_GridSize");
    private static readonly int GrassPositions = Shader.PropertyToID("_GrassPositions");
    private static readonly int GrassHeightMapRT = Shader.PropertyToID("_GrassHeightMapRT");
    private static readonly int GrassMaskMapRT = Shader.PropertyToID("_GrassMaskMapRT");
    private static readonly int BoundsYMinMax = Shader.PropertyToID("_BoundsYMinMax");

    [SerializeField] private LayerMask heightMapLayer;
    [SerializeField] private Material heightMapMat;
    [SerializeField] private ComputeShader computeShader;

    private GrassDataPass _grassDataPass;

    public override void Create()
    {
        _grassDataPass = new GrassDataPass(heightMapLayer, heightMapMat, computeShader)
        {
            // AfterRenderingPrePasses keeps the work early while depth‑texture is valid
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        _grassDataPass.Setup(renderingData);
        renderer.EnqueuePass(_grassDataPass);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _grassDataPass?.Dispose();
    }

    // ---------------------------------------------------------------------
    // INTERNAL PASS (Render Graph‑only, no legacy Configure/Execute calls)
    // ---------------------------------------------------------------------
    private sealed class GrassDataPass : ScriptableRenderPass
    {
        private readonly List<ShaderTagId> _shaderTags = new()
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly")
        };

        // References held only for cleanup
        private RTHandle _heightRT;
        private RTHandle _heightDepthRT;
        private RTHandle _maskRT;
        private RTHandle _colorRT;
        private RTHandle _slopeRT;
        private GraphicsBuffer _grassPositionsBuffer;

        private readonly LayerMask _heightMapLayer;
        private readonly Material _heightMapMat;
        private readonly ComputeShader _computeShader;
        private RenderingData _renderingData;

        public GrassDataPass(LayerMask heightMapLayer, Material heightMapMat, ComputeShader computeShader)
        {
            _heightMapLayer = heightMapLayer;
            _heightMapMat = heightMapMat;
            _computeShader = computeShader;
        }

        public void Setup(in RenderingData renderingData) => _renderingData = renderingData;

        // All work happens here – URP 17.1 ignores Configure/Execute when RecordRenderGraph exists
        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
        {
            if (!InfiniteGrassRenderer.instance || !_heightMapMat || !_computeShader)
                return;

            const int textureSize = 2048;

            AllocateRtHandles(textureSize);

            TextureHandle heightTex = rg.ImportTexture(_heightRT);
            TextureHandle depthTex = rg.ImportTexture(_heightDepthRT);
            TextureHandle maskTex = rg.ImportTexture(_maskRT);
            TextureHandle colorTex = rg.ImportTexture(_colorRT);
            TextureHandle slopeTex = rg.ImportTexture(_slopeRT);

            var camera = _renderingData.cameraData.camera;
            float spacing = InfiniteGrassRenderer.instance.spacing;
            float fullDensityDist = InfiniteGrassRenderer.instance.fullDensityDistance;
            float drawDistance = InfiniteGrassRenderer.instance.drawDistance;
            float densityExp = InfiniteGrassRenderer.instance.densityFalloffExponent;
            float textureThreshold = InfiniteGrassRenderer.instance.textureUpdateThreshold;
            float maxBufferCount = InfiniteGrassRenderer.instance.maxBufferCount;

            Bounds camBounds = CalculateCameraBounds(camera, drawDistance);
            Vector2 centerPos = new(
                Mathf.Floor(camera.transform.position.x / textureThreshold) * textureThreshold,
                Mathf.Floor(camera.transform.position.z / textureThreshold) * textureThreshold);

            // Static ortho from top looking down – fits the area we care about
            Matrix4x4 viewMtx = Matrix4x4.TRS(new Vector3(centerPos.x, camBounds.max.y, centerPos.y), Quaternion.LookRotation(-Vector3.up), new Vector3(1, 1, -1)).inverse;
            Matrix4x4 projMtx = Matrix4x4.Ortho(
                -(drawDistance + textureThreshold), drawDistance + textureThreshold,
                -(drawDistance + textureThreshold), drawDistance + textureThreshold,
                0, camBounds.size.y);

            BuildHeightPass(rg, heightTex, depthTex, viewMtx, projMtx, camBounds);
            BuildMaskPass(rg, maskTex, viewMtx, projMtx);
            BuildColorPass(rg, colorTex, viewMtx, projMtx);
            BuildSlopePass(rg, slopeTex, viewMtx, projMtx);
            BuildComputePass(rg, heightTex, maskTex, colorTex, slopeTex, centerPos, camBounds, spacing, fullDensityDist, densityExp, drawDistance, textureThreshold, maxBufferCount);
        }

        #region Render‑Graph sub‑passes

        private void BuildHeightPass(RenderGraph rg, TextureHandle height, TextureHandle depth, Matrix4x4 view, Matrix4x4 proj, Bounds camBounds)
        {
            var drawSettings = CreateDrawingSettings(_shaderTags, ref _renderingData, _renderingData.cameraData.defaultOpaqueSortFlags);
            _heightMapMat.SetVector(BoundsYMinMax, new Vector2(camBounds.min.y, camBounds.max.y));
            drawSettings.overrideMaterial = _heightMapMat;
            var filterSettings = new FilteringSettings(RenderQueueRange.all, _heightMapLayer);
            var rlDesc = new RendererListParams(_renderingData.cullResults, drawSettings, filterSettings);
            var rl = rg.CreateRendererList(rlDesc);

            using var builder = rg.AddRasterRenderPass<HeightPassData>("Grass Height", out var pass);
            pass.rendererList = rl;
            pass.color = height;
            pass.depth = depth;
            pass.view = view;
            pass.proj = proj;

            builder.SetRenderAttachment(pass.color, 0);
            builder.SetRenderAttachmentDepth(pass.depth);
            builder.UseRendererList(pass.rendererList);
            builder.SetRenderFunc((HeightPassData data, RasterGraphContext ctx) =>
            {
                var cmd = ctx.cmd;
                cmd.SetViewProjectionMatrices(data.view, data.proj);
                cmd.ClearRenderTarget(true, true, Color.black);
                cmd.DrawRendererList(data.rendererList);
            });
        }

        private void BuildMaskPass(RenderGraph rg, TextureHandle mask, Matrix4x4 view, Matrix4x4 proj)
        {
            var drawSettings = CreateDrawingSettings(new ShaderTagId("GrassMask"), ref _renderingData, SortingCriteria.CommonTransparent);
            var filterSettings = new FilteringSettings(RenderQueueRange.all);
            var rlDesc = new RendererListParams(_renderingData.cullResults, drawSettings, filterSettings);
            var rl = rg.CreateRendererList(rlDesc);

            using var builder = rg.AddRasterRenderPass<MaskPassData>("Grass Mask", out var pass);
            pass.rendererList = rl;
            pass.color = mask;
            pass.view = view;
            pass.proj = proj;

            builder.SetRenderAttachment(pass.color, 0);
            builder.UseRendererList(pass.rendererList);
            builder.SetRenderFunc((MaskPassData data, RasterGraphContext ctx) =>
            {
                var cmd = ctx.cmd;
                cmd.SetViewProjectionMatrices(data.view, data.proj);
                cmd.ClearRenderTarget(true, true, Color.clear);
                cmd.DrawRendererList(data.rendererList);
            });
        }

        private void BuildColorPass(RenderGraph rg, TextureHandle color, Matrix4x4 view, Matrix4x4 proj)
        {
            var drawSettings = CreateDrawingSettings(new ShaderTagId("GrassColor"), ref _renderingData, SortingCriteria.CommonTransparent);
            var filterSettings = new FilteringSettings(RenderQueueRange.all);
            var rlDesc = new RendererListParams(_renderingData.cullResults, drawSettings, filterSettings);
            var rl = rg.CreateRendererList(rlDesc);

            using var builder = rg.AddRasterRenderPass<ColorPassData>("Grass Color", out var pass);
            pass.rendererList = rl;
            pass.color = color;
            pass.view = view;
            pass.proj = proj;

            builder.SetRenderAttachment(pass.color, 0);
            builder.UseRendererList(pass.rendererList);
            builder.SetRenderFunc((ColorPassData data, RasterGraphContext ctx) =>
            {
                var cmd = ctx.cmd;
                cmd.SetViewProjectionMatrices(data.view, data.proj);
                cmd.ClearRenderTarget(true, true, Color.clear);
                cmd.DrawRendererList(data.rendererList);
            });
        }

        private void BuildSlopePass(RenderGraph rg, TextureHandle slope, Matrix4x4 view, Matrix4x4 proj)
        {
            var drawSettings = CreateDrawingSettings(new ShaderTagId("GrassSlope"), ref _renderingData, SortingCriteria.CommonTransparent);
            var filterSettings = new FilteringSettings(RenderQueueRange.all);
            var rlDesc = new RendererListParams(_renderingData.cullResults, drawSettings, filterSettings);
            var rl = rg.CreateRendererList(rlDesc);

            using var builder = rg.AddRasterRenderPass<SlopePassData>("Grass Slope", out var pass);
            pass.rendererList = rl;
            pass.color = slope;
            pass.view = view;
            pass.proj = proj;

            builder.SetRenderAttachment(pass.color, 0);
            builder.UseRendererList(pass.rendererList);
            builder.SetRenderFunc((SlopePassData data, RasterGraphContext ctx) =>
            {
                var cmd = ctx.cmd;
                cmd.SetViewProjectionMatrices(data.view, data.proj);
                cmd.ClearRenderTarget(true, true, Color.clear);
                cmd.DrawRendererList(data.rendererList);
            });
        }

        private void BuildComputePass(
            RenderGraph rg,
            TextureHandle height,
            TextureHandle mask,
            TextureHandle color,
            TextureHandle slope,
            Vector2 centerPos,
            Bounds camBounds,
            float spacing,
            float fullDensityDist,
            float densityExp,
            float drawDistance,
            float textureThreshold,
            float maxBufferCount)
        {
            _grassPositionsBuffer?.Release();
            _grassPositionsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Append,
                (int)(1000000 * maxBufferCount),
                sizeof(float) * 4);
            _grassPositionsBuffer.SetCounterValue(0);

            BufferHandle posHandle = rg.ImportBuffer(_grassPositionsBuffer);

            using var builder = rg.AddComputePass<ComputePassData>("Grass Compute", out var pass);
            
            var camera = Camera.main;
            if (!camera)
                camera = Camera.current;
            
            pass.height           = height;
            pass.mask             = mask;
            pass.color            = color;
            pass.slope            = slope;
            pass.positions        = posHandle;
            pass.positionBuffer   = _grassPositionsBuffer;
            pass.cameraVP         = camera.projectionMatrix * camera.worldToCameraMatrix;
            pass.camView          = camera.worldToCameraMatrix;
            pass.camProj          = camera.projectionMatrix;
            pass.centerPos        = centerPos;
            pass.bounds           = camBounds;
            pass.spacing          = spacing;
            pass.fullDensity      = fullDensityDist;
            pass.densityExponent  = densityExp;
            pass.drawDistance     = drawDistance;
            pass.textureThreshold = textureThreshold;

            builder.UseTexture(pass.height);
            builder.UseTexture(pass.mask);
            builder.UseTexture(pass.color);
            builder.UseTexture(pass.slope);
            builder.UseBuffer (pass.positions, AccessFlags.Write);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((ComputePassData d, ComputeGraphContext ctx) =>
            {
                var cmd = ctx.cmd;

                cmd.SetGlobalTexture(GrassColorRT, d.color);
                cmd.SetGlobalTexture(GrassSlopeRT, d.slope);

                _computeShader.SetMatrix(VpMatrix, d.cameraVP);
                _computeShader.SetFloat (FullDensityDistance,   d.fullDensity);
                _computeShader.SetFloat (DensityFalloffExponent,d.densityExponent);
                _computeShader.SetVector(BoundsMin,             d.bounds.min);
                _computeShader.SetVector(BoundsMax,             d.bounds.max);
                _computeShader.SetVector(CameraPosition,        camera.transform.position);
                _computeShader.SetVector(CenterPos,             d.centerPos);
                _computeShader.SetFloat (DrawDistance,          d.drawDistance);
                _computeShader.SetFloat (TextureUpdateThreshold,d.textureThreshold);
                _computeShader.SetFloat (Spacing,               d.spacing);

                Vector2Int gridSize  = new(
                    Mathf.CeilToInt(d.bounds.size.x / d.spacing),
                    Mathf.CeilToInt(d.bounds.size.z / d.spacing));
                Vector2Int gridStart = new(
                    Mathf.FloorToInt(d.bounds.min.x / d.spacing),
                    Mathf.FloorToInt(d.bounds.min.z / d.spacing));

                _computeShader.SetVector(GridStartIndex, (Vector2)gridStart);
                _computeShader.SetVector(GridSize,       (Vector2)gridSize);
                _computeShader.SetBuffer (0, GrassPositions,   d.positionBuffer);
                _computeShader.SetTexture(0, GrassHeightMapRT, d.height);
                _computeShader.SetTexture(0, GrassMaskMapRT,   d.mask);

                cmd.DispatchCompute(_computeShader, 0,
                    Mathf.CeilToInt((float)gridSize.x / 8),
                    Mathf.CeilToInt((float)gridSize.y / 8),
                    1);

                cmd.SetGlobalBuffer(GrassPositions, d.positionBuffer);
                cmd.CopyCounterValue(d.positionBuffer, InfiniteGrassRenderer.instance.argsBuffer, 4);

                if (InfiniteGrassRenderer.instance.previewVisibleGrassCount)
                    cmd.CopyCounterValue(d.positionBuffer, InfiniteGrassRenderer.instance.tBuffer, 0);

                /* --- reset to the camera’s own matrices --- */
                cmd.SetViewProjectionMatrices(d.camView, d.camProj);
            });
        }

        #endregion

        private void AllocateRtHandles(int size)
        {
            var heightDesc = new RenderTextureDescriptor(size, size) { graphicsFormat = GraphicsFormat.R32G32_SFloat };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _heightRT, heightDesc, FilterMode.Bilinear);

            var depthDesc = new RenderTextureDescriptor(size, size)
            {
                graphicsFormat = GraphicsFormat.None,
                depthStencilFormat = GraphicsFormat.D32_SFloat
            };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _heightDepthRT, depthDesc, FilterMode.Bilinear);

            var maskDesc = new RenderTextureDescriptor(size, size) { graphicsFormat = GraphicsFormat.R32_SFloat };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _maskRT, maskDesc, FilterMode.Bilinear);

            var colorDesc = new RenderTextureDescriptor(size, size) { graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _colorRT, colorDesc, FilterMode.Bilinear);

            var slopeDesc = new RenderTextureDescriptor(size, size) { graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat };
            RenderingUtils.ReAllocateHandleIfNeeded(ref _slopeRT, slopeDesc, FilterMode.Bilinear);
        }

        public void Dispose()
        {
            _heightRT?.Release();
            _heightDepthRT?.Release();
            _maskRT?.Release();
            _colorRT?.Release();
            _slopeRT?.Release();
            _grassPositionsBuffer?.Release();
        }

        // --------------------------------------------------------------------------------------
        // SUPPORT TYPES (Render Graph pass‑data structs)
        // --------------------------------------------------------------------------------------
        private sealed class HeightPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public TextureHandle depth;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        private sealed class MaskPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        private sealed class ColorPassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        private sealed class SlopePassData
        {
            public RendererListHandle rendererList;
            public TextureHandle color;
            public Matrix4x4 view;
            public Matrix4x4 proj;
        }

        private sealed class ComputePassData
        {
            public TextureHandle height;
            public TextureHandle mask;
            public TextureHandle color;
            public TextureHandle slope;
            public BufferHandle  positions;
            public GraphicsBuffer positionBuffer;
            public Matrix4x4 cameraVP;
            public Matrix4x4 camView;      // ← new
            public Matrix4x4 camProj;      // ← new
            public Vector2  centerPos;
            public Bounds   bounds;
            public float    spacing;
            public float    fullDensity;
            public float    densityExponent;
            public float    drawDistance;
            public float    textureThreshold;
        }

        // --------------------------------------------------------------------------------------
        // HELPERS
        // --------------------------------------------------------------------------------------
        private static Bounds CalculateCameraBounds(Camera camera, float drawDistance)
        {
            Vector3 ntl = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.nearClipPlane));
            Vector3 ntr = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.nearClipPlane));
            Vector3 nbl = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.nearClipPlane));
            Vector3 nbr = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.nearClipPlane));

            Vector3 ftl = camera.ViewportToWorldPoint(new Vector3(0, 1, drawDistance));
            Vector3 ftr = camera.ViewportToWorldPoint(new Vector3(1, 1, drawDistance));
            Vector3 fbl = camera.ViewportToWorldPoint(new Vector3(0, 0, drawDistance));
            Vector3 fbr = camera.ViewportToWorldPoint(new Vector3(1, 0, drawDistance));

            float startX = new[] { ftl.x, ftr.x, ntl.x, ntr.x, fbl.x, fbr.x, nbl.x, nbr.x }.Max();
            float endX   = new[] { ftl.x, ftr.x, ntl.x, ntr.x, fbl.x, fbr.x, nbl.x, nbr.x }.Min();
            float startY = new[] { ftl.y, ftr.y, ntl.y, ntr.y, fbl.y, fbr.y, nbl.y, nbr.y }.Max();
            float endY   = new[] { ftl.y, ftr.y, ntl.y, ntr.y, fbl.y, fbr.y, nbl.y, nbr.y }.Min();
            float startZ = new[] { ftl.z, ftr.z, ntl.z, ntr.z, fbl.z, fbr.z, nbl.z, nbr.z }.Max();
            float endZ   = new[] { ftl.z, ftr.z, ntl.z, ntr.z, fbl.z, fbr.z, nbl.z, nbr.z }.Min();

            Vector3 center = new((startX + endX) * 0.5f, (startY + endY) * 0.5f, (startZ + endZ) * 0.5f);
            Vector3 size   = new(Mathf.Abs(startX - endX), Mathf.Abs(startY - endY), Mathf.Abs(startZ - endZ));

            Bounds b = new(center, size);
            b.Expand(1);
            return b;
        }
    }
}
