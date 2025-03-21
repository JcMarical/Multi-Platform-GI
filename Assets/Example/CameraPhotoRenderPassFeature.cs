using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class CameraPhotoRenderPassFeature : ScriptableRendererFeature
{


    class CustomRenderPass : ScriptableRenderPass
    {
        static string rt_name = "_ExampleRT";
        static int rt_ID = Shader.PropertyToID(rt_name);



        static string blitShader_Name = "Example/BlitShader";

        static Shader blitShader = Shader.Find(blitShader_Name);

        RTHandle _cameraColorTgt;

        Material blitMaterial = new Material(blitShader);

        public void SetUP(RTHandle cameraColor)
        {
            _cameraColorTgt = cameraColor;
        }


        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(2560, 1440, RenderTextureFormat.Default,0);
            cmd.GetTemporaryRT(rt_ID,descriptor);
            ConfigureTarget(rt_ID);
            //ConfigureTarget(_cameraColorTgt);
            ConfigureClear(ClearFlag.Color,Color.black);
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("tmpCmd");
            cmd.Blit(renderingData.cameraData.renderer.cameraColorTargetHandle, rt_ID , blitMaterial);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            cmd.Release();

        }

        // Cleanup any allocated resources that were created during the execution of this render pass.
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(rt_ID);
        }
    }

    CustomRenderPass m_ScriptablePass;

    /// <inheritdoc/>
    public override void Create()
    {
        m_ScriptablePass = new CustomRenderPass();

        // Configures where the render pass should be injected.
        m_ScriptablePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
    }
}


