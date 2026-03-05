using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class GrassDataRendererFeature : ScriptableRendererFeature
{
    [SerializeField] private LayerMask heightMapLayer;
    [SerializeField] private Material heightMapMat;
    [SerializeField] private ComputeShader computeShader;

    GrassDataPass grassDataPass;

    public override void Create()
    {
        grassDataPass = new GrassDataPass(heightMapLayer, heightMapMat, computeShader);
        grassDataPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(grassDataPass);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            grassDataPass.Dispose();
        }
    }

    private class GrassDataPass : ScriptableRenderPass
    {
        private List<ShaderTagId> shaderTagsList = new List<ShaderTagId>();

        private RTHandle heightRT;
        private RTHandle heightDepthRT;
        private RTHandle maskRT;
        private RTHandle colorRT;
        private RTHandle slopeRT;

        private LayerMask heightMapLayer;
        private Material heightMapMat;

        private ComputeShader computeShader;

        private ComputeBuffer grassPositionsBuffer;

        public GrassDataPass(LayerMask heightMapLayer, Material heightMapMat, ComputeShader computeShader)
        {
            this.heightMapLayer = heightMapLayer;
            this.computeShader = computeShader;
            this.heightMapMat = heightMapMat;

            shaderTagsList.Add(new ShaderTagId("SRPDefaultUnlit"));
            shaderTagsList.Add(new ShaderTagId("UniversalForward"));
            shaderTagsList.Add(new ShaderTagId("UniversalForwardOnly"));
        }

        private void EnsureRTHandles()
        {
            int textureSize = 2048;
            RenderingUtils.ReAllocateIfNeeded(ref heightRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RGFloat, 0), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref heightDepthRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RFloat, 32), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref maskRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.RFloat, 0), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref colorRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.ARGBFloat, 0), FilterMode.Bilinear);
            RenderingUtils.ReAllocateIfNeeded(ref slopeRT, new RenderTextureDescriptor(textureSize, textureSize, RenderTextureFormat.ARGBFloat, 0), FilterMode.Bilinear);
        }

        private class PassData
        {
            public TextureHandle heightTexture;
            public TextureHandle heightDepthTexture;
            public TextureHandle maskTexture;
            public TextureHandle colorTexture;
            public TextureHandle slopeTexture;

            public RTHandle heightRTHandle;
            public RTHandle maskRTHandle;

            public TextureHandle cameraColorTarget;
            public TextureHandle cameraDepthTarget;

            public RendererListHandle heightMapRendererList;
            public RendererListHandle maskRendererList;
            public RendererListHandle colorRendererList;
            public RendererListHandle slopeRendererList;

            public Material heightMapMat;
            public ComputeShader computeShader;

            public Matrix4x4 viewMatrix;
            public Matrix4x4 projectionMatrix;
            public Matrix4x4 originalViewMatrix;
            public Matrix4x4 originalProjectionMatrix;

            public Vector2 centerPos;
            public Bounds cameraBounds;
            public float spacing;
            public float fullDensityDistance;
            public float drawDistance;
            public float maxBufferCount;
            public float textureUpdateThreshold;
            public Vector2Int gridSize;
            public Vector2Int gridStartIndex;
            public Matrix4x4 cameraVPMatrix;
            public Vector3 cameraPosition;

            public ComputeBuffer grassPositionsBuffer;
            public ComputeBuffer argsBuffer;
            public ComputeBuffer tBuffer;
            public bool previewVisibleGrassCount;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
        {
            if (InfiniteGrassRenderer.instance == null || heightMapMat == null || computeShader == null)
                return;

            EnsureRTHandles();

            UniversalRenderingData renderingData = frameContext.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameContext.Get<UniversalCameraData>();
            UniversalLightData lightData = frameContext.Get<UniversalLightData>();

            Camera cam = cameraData.camera;

            float spacing = InfiniteGrassRenderer.instance.spacing;
            float fullDensityDistance = InfiniteGrassRenderer.instance.fullDensityDistance;
            float drawDistance = InfiniteGrassRenderer.instance.drawDistance;
            float maxBufferCount = InfiniteGrassRenderer.instance.maxBufferCount;
            float textureUpdateThreshold = InfiniteGrassRenderer.instance.textureUpdateThreshold;

            if (spacing == 0) return;

            Bounds cameraBounds = CalculateCameraBounds(cam, drawDistance);
            Vector2 centerPos = new Vector2(
                Mathf.Floor(cam.transform.position.x / textureUpdateThreshold) * textureUpdateThreshold,
                Mathf.Floor(cam.transform.position.z / textureUpdateThreshold) * textureUpdateThreshold
            );

            Matrix4x4 viewMatrix = Matrix4x4.TRS(
                new Vector3(centerPos.x, cameraBounds.max.y, centerPos.y),
                Quaternion.LookRotation(-Vector3.up),
                new Vector3(1, 1, -1)
            ).inverse;
            Matrix4x4 projectionMatrix = Matrix4x4.Ortho(
                -(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold,
                -(drawDistance + textureUpdateThreshold), drawDistance + textureUpdateThreshold,
                0, cameraBounds.size.y
            );

            Vector2Int gridSize = new Vector2Int(
                Mathf.CeilToInt(cameraBounds.size.x / spacing),
                Mathf.CeilToInt(cameraBounds.size.z / spacing)
            );
            Vector2Int gridStartIndex = new Vector2Int(
                Mathf.FloorToInt(cameraBounds.min.x / spacing),
                Mathf.FloorToInt(cameraBounds.min.z / spacing)
            );

            grassPositionsBuffer?.Release();
            grassPositionsBuffer = new ComputeBuffer(
                (int)(1000000 * maxBufferCount), sizeof(float) * 3, ComputeBufferType.Append
            );

            // --- Build renderer lists ---

            // Height map: override material on heightMapLayer objects
            var heightDrawSettings = RenderingUtils.CreateDrawingSettings(
                shaderTagsList, renderingData, cameraData, lightData, cameraData.defaultOpaqueSortFlags
            );
            heightMapMat.SetVector("_BoundsYMinMax", new Vector2(cameraBounds.min.y, cameraBounds.max.y));
            heightDrawSettings.overrideMaterial = heightMapMat;
            var heightFilterSettings = new FilteringSettings(RenderQueueRange.all, heightMapLayer);

            // Mask pass
            var maskShaderTag = new List<ShaderTagId> { new ShaderTagId("GrassMask") };
            var maskDrawSettings = RenderingUtils.CreateDrawingSettings(
                maskShaderTag, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent
            );
            var maskFilterSettings = new FilteringSettings(RenderQueueRange.all);

            // Color pass
            var colorShaderTag = new List<ShaderTagId> { new ShaderTagId("GrassColor") };
            var colorDrawSettings = RenderingUtils.CreateDrawingSettings(
                colorShaderTag, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent
            );
            var colorFilterSettings = new FilteringSettings(RenderQueueRange.all);

            // Slope pass
            var slopeShaderTag = new List<ShaderTagId> { new ShaderTagId("GrassSlope") };
            var slopeDrawSettings = RenderingUtils.CreateDrawingSettings(
                slopeShaderTag, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent
            );
            var slopeFilterSettings = new FilteringSettings(RenderQueueRange.all);

            UniversalResourceData resourceData = frameContext.Get<UniversalResourceData>();

            using (var builder = renderGraph.AddUnsafePass<PassData>("Grass Data Pass", out var passData))
            {
                passData.heightTexture = renderGraph.ImportTexture(heightRT);
                passData.heightDepthTexture = renderGraph.ImportTexture(heightDepthRT);
                passData.maskTexture = renderGraph.ImportTexture(maskRT);
                passData.colorTexture = renderGraph.ImportTexture(colorRT);
                passData.slopeTexture = renderGraph.ImportTexture(slopeRT);

                builder.UseTexture(passData.heightTexture, AccessFlags.ReadWrite);
                builder.UseTexture(passData.heightDepthTexture, AccessFlags.ReadWrite);
                builder.UseTexture(passData.maskTexture, AccessFlags.ReadWrite);
                builder.UseTexture(passData.colorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(passData.slopeTexture, AccessFlags.ReadWrite);

                passData.cameraColorTarget = resourceData.activeColorTexture;
                passData.cameraDepthTarget = resourceData.activeDepthTexture;
                builder.UseTexture(passData.cameraColorTarget, AccessFlags.Write);
                builder.UseTexture(passData.cameraDepthTarget, AccessFlags.Write);

                passData.heightMapRendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, heightDrawSettings, heightFilterSettings)
                );
                passData.maskRendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, maskDrawSettings, maskFilterSettings)
                );
                passData.colorRendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, colorDrawSettings, colorFilterSettings)
                );
                passData.slopeRendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, slopeDrawSettings, slopeFilterSettings)
                );

                builder.UseRendererList(passData.heightMapRendererList);
                builder.UseRendererList(passData.maskRendererList);
                builder.UseRendererList(passData.colorRendererList);
                builder.UseRendererList(passData.slopeRendererList);

                passData.heightRTHandle = heightRT;
                passData.maskRTHandle = maskRT;

                passData.heightMapMat = heightMapMat;
                passData.computeShader = computeShader;
                passData.viewMatrix = viewMatrix;
                passData.projectionMatrix = projectionMatrix;
                passData.originalViewMatrix = cameraData.GetViewMatrix();
                passData.originalProjectionMatrix = cameraData.GetProjectionMatrix();
                passData.centerPos = centerPos;
                passData.cameraBounds = cameraBounds;
                passData.spacing = spacing;
                passData.fullDensityDistance = fullDensityDistance;
                passData.drawDistance = drawDistance;
                passData.maxBufferCount = maxBufferCount;
                passData.textureUpdateThreshold = textureUpdateThreshold;
                passData.gridSize = gridSize;
                passData.gridStartIndex = gridStartIndex;
                passData.cameraVPMatrix = cameraData.GetProjectionMatrix() * cameraData.GetViewMatrix();
                passData.cameraPosition = cam.transform.position;
                passData.grassPositionsBuffer = grassPositionsBuffer;
                passData.argsBuffer = InfiniteGrassRenderer.instance.argsBuffer;
                passData.tBuffer = InfiniteGrassRenderer.instance.tBuffer;
                passData.previewVisibleGrassCount = InfiniteGrassRenderer.instance.previewVisibleGrassCount;

                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }

        static void SetAllViewProjMatrices(CommandBuffer cmd, Matrix4x4 view, Matrix4x4 proj)
        {
            // SetViewProjectionMatrices sets the built-in cbuffer matrices
            // (unity_MatrixV, unity_MatrixP, unity_MatrixVP etc.)
            cmd.SetViewProjectionMatrices(view, proj);

            // Also set the SRP global keyword variants that URP HLSL shaders
            // and UnityCG.cginc macros may resolve to on some platforms.
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(proj, true);
            Matrix4x4 vp = gpuProj * view;
            cmd.SetGlobalMatrix(ShaderPropertyId.viewMatrix, view);
            cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewMatrix, view.inverse);
            cmd.SetGlobalMatrix(ShaderPropertyId.projectionMatrix, gpuProj);
            cmd.SetGlobalMatrix(ShaderPropertyId.viewAndProjectionMatrix, vp);
        }

        private static class ShaderPropertyId
        {
            public static readonly int viewMatrix = Shader.PropertyToID("unity_MatrixV");
            public static readonly int inverseViewMatrix = Shader.PropertyToID("unity_MatrixInvV");
            public static readonly int projectionMatrix = Shader.PropertyToID("unity_MatrixP");
            public static readonly int viewAndProjectionMatrix = Shader.PropertyToID("unity_MatrixVP");
        }

        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            // Override to top-down orthographic view for data texture rendering
            SetAllViewProjMatrices(cmd, data.viewMatrix, data.projectionMatrix);

            // --- Height Map ---
            cmd.SetRenderTarget(data.heightTexture, data.heightDepthTexture);
            cmd.ClearRenderTarget(true, true, Color.black);
            cmd.DrawRendererList(data.heightMapRendererList);

            // --- Mask ---
            cmd.SetRenderTarget(data.maskTexture);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));
            cmd.DrawRendererList(data.maskRendererList);

            // --- Color ---
            cmd.SetRenderTarget(data.colorTexture);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));
            cmd.DrawRendererList(data.colorRendererList);

            // --- Slope ---
            cmd.SetRenderTarget(data.slopeTexture);
            cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));
            cmd.DrawRendererList(data.slopeRendererList);

            cmd.SetGlobalTexture("_GrassColorRT", data.colorTexture);
            cmd.SetGlobalTexture("_GrassSlopeRT", data.slopeTexture);

            // Restore camera render target and matrices
            cmd.SetRenderTarget(data.cameraColorTarget, data.cameraDepthTarget);
            SetAllViewProjMatrices(cmd, data.originalViewMatrix, data.originalProjectionMatrix);

            // --- Compute grass positions ---
            ComputeShader cs = data.computeShader;
            ComputeBuffer posBuffer = data.grassPositionsBuffer;

            cs.SetMatrix("_VPMatrix", data.cameraVPMatrix);
            cs.SetFloat("_FullDensityDistance", data.fullDensityDistance);
            cs.SetVector("_BoundsMin", data.cameraBounds.min);
            cs.SetVector("_BoundsMax", data.cameraBounds.max);
            cs.SetVector("_CameraPosition", data.cameraPosition);
            cs.SetVector("_CenterPos", data.centerPos);
            cs.SetFloat("_DrawDistance", data.drawDistance);
            cs.SetFloat("_TextureUpdateThreshold", data.textureUpdateThreshold);
            cs.SetFloat("_Spacing", data.spacing);
            cs.SetVector("_GridStartIndex", (Vector2)data.gridStartIndex);
            cs.SetVector("_GridSize", (Vector2)data.gridSize);
            cs.SetBuffer(0, "_GrassPositions", posBuffer);
            cs.SetTexture(0, "_GrassHeightMapRT", data.heightRTHandle);
            cs.SetTexture(0, "_GrassMaskMapRT", data.maskRTHandle);

            posBuffer.SetCounterValue(0);

            cmd.DispatchCompute(cs, 0,
                Mathf.CeilToInt((float)data.gridSize.x / 8),
                Mathf.CeilToInt((float)data.gridSize.y / 8), 1);

            cmd.SetGlobalBuffer("_GrassPositions", posBuffer);

            if (data.argsBuffer != null)
                cmd.CopyCounterValue(posBuffer, data.argsBuffer, 4);

            if (data.previewVisibleGrassCount && data.tBuffer != null)
                cmd.CopyCounterValue(posBuffer, data.tBuffer, 0);
        }

        Bounds CalculateCameraBounds(Camera camera, float drawDistance)
        {
            Vector3 ntopLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, camera.nearClipPlane));
            Vector3 ntopRight = camera.ViewportToWorldPoint(new Vector3(1, 1, camera.nearClipPlane));
            Vector3 nbottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, camera.nearClipPlane));
            Vector3 nbottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, camera.nearClipPlane));

            Vector3 ftopLeft = camera.ViewportToWorldPoint(new Vector3(0, 1, drawDistance));
            Vector3 ftopRight = camera.ViewportToWorldPoint(new Vector3(1, 1, drawDistance));
            Vector3 fbottomLeft = camera.ViewportToWorldPoint(new Vector3(0, 0, drawDistance));
            Vector3 fbottomRight = camera.ViewportToWorldPoint(new Vector3(1, 0, drawDistance));

            float[] xValues = new float[] { ftopLeft.x, ftopRight.x, ntopLeft.x, ntopRight.x, fbottomLeft.x, fbottomRight.x, nbottomLeft.x, nbottomRight.x };
            float startX = xValues.Max();
            float endX = xValues.Min();

            float[] yValues = new float[] { ftopLeft.y, ftopRight.y, ntopLeft.y, ntopRight.y, fbottomLeft.y, fbottomRight.y, nbottomLeft.y, nbottomRight.y };
            float startY = yValues.Max();
            float endY = yValues.Min();

            float[] zValues = new float[] { ftopLeft.z, ftopRight.z, ntopLeft.z, ntopRight.z, fbottomLeft.z, fbottomRight.z, nbottomLeft.z, nbottomRight.z };
            float startZ = zValues.Max();
            float endZ = zValues.Min();

            Vector3 center = new Vector3((startX + endX) / 2, (startY + endY) / 2, (startZ + endZ) / 2);
            Vector3 size = new Vector3(Mathf.Abs(startX - endX), Mathf.Abs(startY - endY), Mathf.Abs(startZ - endZ));

            Bounds bounds = new Bounds(center, size);
            bounds.Expand(1);
            return bounds;
        }

        public void Dispose()
        {
            heightRT?.Release();
            heightDepthRT?.Release();
            maskRT?.Release();
            colorRT?.Release();
            slopeRT?.Release();
            grassPositionsBuffer?.Release();
        }
    }

}


