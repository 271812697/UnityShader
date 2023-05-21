using UnityEngine;
using UnityEditor;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Collections;

public class Lighting
{

    const string bufferName = "Lighting";

    const int maxDirLightCount = 4;

    static int
        dirLightCountId = Shader.PropertyToID("_DirectionalLightCount"),
        dirLightColorsId = Shader.PropertyToID("_DirectionalLightColors"),
        dirLightDirectionsId = Shader.PropertyToID("_DirectionalLightDirections");

    static Vector4[]
        dirLightColors = new Vector4[maxDirLightCount],
        dirLightDirections = new Vector4[maxDirLightCount];

    CommandBuffer buffer = new CommandBuffer
    {
        name = bufferName
    };

    CullingResults cullingResults;

    public void Setup(
        ScriptableRenderContext context, CullingResults cullingResults
    )
    {
        this.cullingResults = cullingResults;
        buffer.BeginSample(bufferName);
        SetupLights();
        buffer.EndSample(bufferName);
        context.ExecuteCommandBuffer(buffer);
        buffer.Clear();
    }

    void SetupLights()
    {
        NativeArray<VisibleLight> visibleLights = cullingResults.visibleLights;
        int dirLightCount = 0;
        for (int i = 0; i < visibleLights.Length; i++)
        {
            VisibleLight visibleLight = visibleLights[i];
            if (visibleLight.lightType == LightType.Directional)
            {
                SetupDirectionalLight(dirLightCount++, ref visibleLight);
                if (dirLightCount >= maxDirLightCount)
                {
                    break;
                }
            }
        }

        buffer.SetGlobalInt(dirLightCountId, dirLightCount);
        buffer.SetGlobalVectorArray(dirLightColorsId, dirLightColors);
        buffer.SetGlobalVectorArray(dirLightDirectionsId, dirLightDirections);
    }

    void SetupDirectionalLight(int index, ref VisibleLight visibleLight)
    {
        dirLightColors[index] = visibleLight.finalColor;
        dirLightDirections[index] = -visibleLight.localToWorldMatrix.GetColumn(2);
    }
}
public class CustomRenderPipeline : RenderPipeline
{

    //CameraRenderer renderer = new CameraRenderer();
    const string bufferName = "Render Camera";
    static ShaderTagId
    unlitShaderTagId = new ShaderTagId("SRPDefaultUnlit"),
    litShaderTagId = new ShaderTagId("CustomLit");
    bool useDynamicBatching, useGPUInstancing;
    Lighting lighting = new Lighting();
    CommandBuffer buffer = new CommandBuffer
    {
        name = bufferName
    };
    static ShaderTagId[] legacyShaderTagIds = {
        new ShaderTagId("Always"),
        new ShaderTagId("ForwardBase"),
        new ShaderTagId("PrepassBase"),
        new ShaderTagId("Vertex"),
        new ShaderTagId("VertexLMRGBM"),
        new ShaderTagId("VertexLM")
    };

    static Material errorMaterial;
    public CustomRenderPipeline(
        bool useDynamicBatching, bool useGPUInstancing, bool useSRPBatcher
    )
    {
        this.useDynamicBatching = useDynamicBatching;
        this.useGPUInstancing = useGPUInstancing;
        GraphicsSettings.useScriptableRenderPipelineBatching = useSRPBatcher;
        GraphicsSettings.lightsUseLinearIntensity = true;
    }

    protected override void Render(
        ScriptableRenderContext context, Camera[] cameras
    )
    {
        foreach (Camera camera in cameras)
        {
            Profiler.BeginSample("Editor Only");
            buffer.name  = camera.name;
            Profiler.EndSample();
            if (camera.cameraType == CameraType.SceneView)
            {
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
        
            }
            //Cull
            CullingResults cullingResults;
            if (camera.TryGetCullingParameters(out ScriptableCullingParameters p))
            {
                cullingResults = context.Cull(ref p);

            }
            else return;

            //Setup
            context.SetupCameraProperties(camera);
            CameraClearFlags flags = camera.clearFlags;
            buffer.ClearRenderTarget(
                flags <= CameraClearFlags.Depth,
                flags == CameraClearFlags.Color,
                flags == CameraClearFlags.Color ?
                    camera.backgroundColor.linear : Color.clear
            );
            buffer.BeginSample(camera.name);
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();

            lighting.Setup(context, cullingResults);

            //DrawVisibleGeometry
            {
                var sortingSettings = new SortingSettings(camera)
                {
                    criteria = SortingCriteria.CommonOpaque
                };
                var drawingSettings = new DrawingSettings(
                    unlitShaderTagId, sortingSettings
                )
                {
                    enableDynamicBatching = useDynamicBatching,
                    enableInstancing = useGPUInstancing
                };
                drawingSettings.SetShaderPassName(1, litShaderTagId);

                var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);

                context.DrawRenderers(
                    cullingResults, ref drawingSettings, ref filteringSettings
                );

                context.DrawSkybox(camera);

                sortingSettings.criteria = SortingCriteria.CommonTransparent;
                drawingSettings.sortingSettings = sortingSettings;
                filteringSettings.renderQueueRange = RenderQueueRange.transparent;

                context.DrawRenderers(
                    cullingResults, ref drawingSettings, ref filteringSettings
                );
            }



            //DrawUnsupportedShaders
            {
                if (errorMaterial == null)
                {
                    errorMaterial =
                        new Material(Shader.Find("Hidden/InternalErrorShader"));
                }
                var drawingSettings = new DrawingSettings(
                    legacyShaderTagIds[0], new SortingSettings(camera)
                )
                {
                    overrideMaterial = errorMaterial
                };
                for (int i = 1; i < legacyShaderTagIds.Length; i++)
                {
                    drawingSettings.SetShaderPassName(i, legacyShaderTagIds[i]);
                }
                var filteringSettings = FilteringSettings.defaultValue;
                context.DrawRenderers(
                    cullingResults, ref drawingSettings, ref filteringSettings
                );
            } 
            
            //DrawGizmos
            if (Handles.ShouldRenderGizmos())
            {
                 context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
                 context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
            }

            buffer.EndSample(camera.name);
            context.ExecuteCommandBuffer(buffer);
            buffer.Clear();
            context.Submit();
        }
    }
}