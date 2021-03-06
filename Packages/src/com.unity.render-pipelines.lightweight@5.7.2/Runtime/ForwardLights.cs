using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;
using Unity.Collections;

namespace UnityEngine.Rendering.LWRP
{
    internal class ForwardLights
    {
        static class LightConstantBuffer
        {
            public static int _MainLightPosition;
            public static int _MainLightColor;
            public static int _MainLightLightMat;
            public static int _MainlightCookie;
            public static int _MainLightCookieSize;
            public static int _MainLightCookieBlend;
            public static int _MainLightCookieFalloff;
            public static int _MainLightCookieColor;
            public static int _AdditionalLightsCount;
            public static int _AdditionalLightsPosition;
            public static int _AdditionalLightsColor;
            public static int _AdditionalLightsAttenuation;
            public static int _AdditionalLightsSpotDir;
            
        }

        class LightCookie
        {
            public Texture Tex;
            public Matrix4x4 LightMat;
            public float CookieSize;
            public float Falloff;
            public Color cookieColor;
        }
        
        const string k_SetupLightConstants = "Setup Light Constants";
        MixedLightingSetup m_MixedLightingSetup;

        Vector4 k_DefaultLightPosition = new Vector4(0.0f, 0.0f, 1.0f, 1.0f);
        Vector4 k_DefaultLightColor = Color.black;
        Vector4 k_DefaultLightAttenuation = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
        Vector4 k_DefaultLightSpotDirection = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);

        Vector4[] m_AdditionalLightPositions;
        Vector4[] m_AdditionalLightColors;
        Vector4[] m_AdditionalLightAttenuations;
        Vector4[] m_AdditionalLightSpotDirections;
        
        public ForwardLights()
        {
            LightConstantBuffer._MainLightPosition = Shader.PropertyToID("_MainLightPosition");
            LightConstantBuffer._MainLightColor = Shader.PropertyToID("_MainLightColor");
            LightConstantBuffer._MainLightLightMat = Shader.PropertyToID("_MainLightWorldToLight");
            LightConstantBuffer._MainLightCookieSize = Shader.PropertyToID("_MainLightCookieSize");
            LightConstantBuffer._MainLightCookieFalloff = Shader.PropertyToID("_MainLightCookieFalloff");
            LightConstantBuffer._MainLightCookieColor = Shader.PropertyToID("_MainLightCookieColor");


            LightConstantBuffer._AdditionalLightsCount = Shader.PropertyToID("_AdditionalLightsCount");
            LightConstantBuffer._AdditionalLightsPosition = Shader.PropertyToID("_AdditionalLightsPosition");
            LightConstantBuffer._AdditionalLightsColor = Shader.PropertyToID("_AdditionalLightsColor");
            LightConstantBuffer._AdditionalLightsAttenuation = Shader.PropertyToID("_AdditionalLightsAttenuation");
            LightConstantBuffer._AdditionalLightsSpotDir = Shader.PropertyToID("_AdditionalLightsSpotDir");
            LightConstantBuffer._MainlightCookie = Shader.PropertyToID("_LightTexture0");

            int maxLights = LightweightRenderPipeline.maxVisibleAdditionalLights;
            m_AdditionalLightPositions = new Vector4[maxLights];
            m_AdditionalLightColors = new Vector4[maxLights];
            m_AdditionalLightAttenuations = new Vector4[maxLights];
            m_AdditionalLightSpotDirections = new Vector4[maxLights];
        }

        public void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            SetupPerObjectLightIndices(renderingData.cullResults, ref renderingData.lightData);
            
            int additionalLightsCount = renderingData.lightData.additionalLightsCount;
            bool additionalLightsPerVertex = renderingData.lightData.shadeAdditionalLightsPerVertex;
            CommandBuffer cmd = CommandBufferPool.Get(k_SetupLightConstants);
            SetupShaderLightConstants(cmd, ref renderingData.lightData);

            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsVertex,
                additionalLightsCount > 0 && additionalLightsPerVertex);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsPixel,
                additionalLightsCount > 0 && !additionalLightsPerVertex);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MixedLightingSubtractive,
                renderingData.lightData.supportsMixedLighting &&
                m_MixedLightingSetup == MixedLightingSetup.Subtractive);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void InitializeLightConstants(NativeArray<VisibleLight> lights, int lightIndex, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightAttenuation, out Vector4 lightSpotDir, out LightCookie cookie)
        {
            lightPos = k_DefaultLightPosition;
            lightColor = k_DefaultLightColor;
            lightAttenuation = k_DefaultLightAttenuation;
            lightSpotDir = k_DefaultLightSpotDirection;
            cookie = null;
            // When no lights are visible, main light will be set to -1.
            // In this case we initialize it to default values and return
            if (lightIndex < 0)
                return;

            VisibleLight lightData = lights[lightIndex];
            if (lightData.lightType == LightType.Directional)
            {
                Vector4 dir = -lightData.localToWorldMatrix.GetColumn(2);
                lightPos = new Vector4(dir.x, dir.y, dir.z, 1.0f);
            }
            else
            {
                Vector4 pos = lightData.localToWorldMatrix.GetColumn(3);
                lightPos = new Vector4(pos.x, pos.y, pos.z, 1.0f);
            }

            // VisibleLight.finalColor already returns color in active color space
            lightColor = lightData.finalColor;

            // Directional Light attenuation is initialize so distance attenuation always be 1.0
            if (lightData.lightType != LightType.Directional)
            {
                // Light attenuation in lightweight matches the unity vanilla one.
                // attenuation = 1.0 / distanceToLightSqr
                // We offer two different smoothing factors.
                // The smoothing factors make sure that the light intensity is zero at the light range limit.
                // The first smoothing factor is a linear fade starting at 80 % of the light range.
                // smoothFactor = (lightRangeSqr - distanceToLightSqr) / (lightRangeSqr - fadeStartDistanceSqr)
                // We rewrite smoothFactor to be able to pre compute the constant terms below and apply the smooth factor
                // with one MAD instruction
                // smoothFactor =  distanceSqr * (1.0 / (fadeDistanceSqr - lightRangeSqr)) + (-lightRangeSqr / (fadeDistanceSqr - lightRangeSqr)
                //                 distanceSqr *           oneOverFadeRangeSqr             +              lightRangeSqrOverFadeRangeSqr

                // The other smoothing factor matches the one used in the Unity lightmapper but is slower than the linear one.
                // smoothFactor = (1.0 - saturate((distanceSqr * 1.0 / lightrangeSqr)^2))^2
                float lightRangeSqr = lightData.range * lightData.range;
                float fadeStartDistanceSqr = 0.8f * 0.8f * lightRangeSqr;
                float fadeRangeSqr = (fadeStartDistanceSqr - lightRangeSqr);
                float oneOverFadeRangeSqr = 1.0f / fadeRangeSqr;
                float lightRangeSqrOverFadeRangeSqr = -lightRangeSqr / fadeRangeSqr;
                float oneOverLightRangeSqr = 1.0f / Mathf.Max(0.0001f, lightData.range * lightData.range);

                // On mobile: Use the faster linear smoothing factor.
                // On other devices: Use the smoothing factor that matches the GI.
                lightAttenuation.x = Application.isMobilePlatform ? oneOverFadeRangeSqr : oneOverLightRangeSqr;
                lightAttenuation.y = lightRangeSqrOverFadeRangeSqr;
            }

            if (lightData.lightType == LightType.Spot)
            {
                Vector4 dir = lightData.localToWorldMatrix.GetColumn(2);
                lightSpotDir = new Vector4(-dir.x, -dir.y, -dir.z, 0.0f);

                // Spot Attenuation with a linear falloff can be defined as
                // (SdotL - cosOuterAngle) / (cosInnerAngle - cosOuterAngle)
                // This can be rewritten as
                // invAngleRange = 1.0 / (cosInnerAngle - cosOuterAngle)
                // SdotL * invAngleRange + (-cosOuterAngle * invAngleRange)
                // If we precompute the terms in a MAD instruction
                float cosOuterAngle = Mathf.Cos(Mathf.Deg2Rad * lightData.spotAngle * 0.5f);
                // We neeed to do a null check for particle lights
                // This should be changed in the future
                // Particle lights will use an inline function
                float cosInnerAngle;
                if (lightData.light != null)
                    cosInnerAngle = Mathf.Cos(LightmapperUtils.ExtractInnerCone(lightData.light) * 0.5f);
                else
                    cosInnerAngle = Mathf.Cos((2.0f * Mathf.Atan(Mathf.Tan(lightData.spotAngle * 0.5f * Mathf.Deg2Rad) * (64.0f - 18.0f) / 64.0f)) * 0.5f);
                float smoothAngleRange = Mathf.Max(0.001f, cosInnerAngle - cosOuterAngle);
                float invAngleRange = 1.0f / smoothAngleRange;
                float add = -cosOuterAngle * invAngleRange;
                lightAttenuation.z = invAngleRange;
                lightAttenuation.w = add;
            }

            Light light = lightData.light;
            Texture lightTex = light.cookie;
            LWRPAdditionalLightData data = light.gameObject.GetComponent<LWRPAdditionalLightData>();
            if (lightTex && data)
            {
                cookie = new LightCookie
                {
                    Tex = lightTex,
                    LightMat = light.transform.worldToLocalMatrix,
                    CookieSize = light.cookieSize,
                    Falloff = data.lightCookieFalloff,
                    cookieColor = data.lightCookieColor,
                };
            }
            if (light != null && light.bakingOutput.mixedLightingMode == MixedLightingMode.Subtractive && light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
            {
                if (m_MixedLightingSetup == MixedLightingSetup.None && lightData.light.shadows != LightShadows.None)
                {
                    m_MixedLightingSetup = MixedLightingSetup.Subtractive;
                }
            }
        }

        void SetupShaderLightConstants(CommandBuffer cmd, ref LightData lightData)
        {
            // Clear to default all light constant data
            LightCookie lightcookie;
            for (int i = 0; i < LightweightRenderPipeline.maxVisibleAdditionalLights; ++i)
                InitializeLightConstants(lightData.visibleLights, -1, out m_AdditionalLightPositions[i],
                    out m_AdditionalLightColors[i],
                    out m_AdditionalLightAttenuations[i],
                    out m_AdditionalLightSpotDirections[i], out lightcookie);

            m_MixedLightingSetup = MixedLightingSetup.None;

            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Lightweight pipeline also supports only a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, ref lightData);
            SetupAdditionalLightConstants(cmd, ref lightData);
        }

        void SetupMainLightConstants(CommandBuffer cmd, ref LightData lightData)
        {
            LightCookie lightcookie;
            Vector4 lightPos, lightColor, lightAttenuation, lightSpotDir;
            InitializeLightConstants(lightData.visibleLights, lightData.mainLightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightcookie);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightPosition, lightPos);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightColor, lightColor);
            if(lightcookie != null)
            {
                cmd.EnableShaderKeyword("_LIGHTCOOKIE");
                cmd.SetGlobalMatrix(LightConstantBuffer._MainLightLightMat, lightcookie.LightMat);
                cmd.SetGlobalFloat(LightConstantBuffer._MainLightCookieSize, lightcookie.CookieSize);
                cmd.SetGlobalTexture(LightConstantBuffer._MainlightCookie, lightcookie.Tex);
                cmd.SetGlobalFloat(LightConstantBuffer._MainLightCookieFalloff, lightcookie.Falloff);
                cmd.SetGlobalColor(LightConstantBuffer._MainLightCookieColor, lightcookie.cookieColor);
            }
            else
            {
                cmd.DisableShaderKeyword("_LIGHTCOOKIE");
            }
        }

        void SetupAdditionalLightConstants(CommandBuffer cmd, ref LightData lightData)
        {
            int maxVisibleAdditionalLights = LightweightRenderPipeline.maxVisibleAdditionalLights;
            var lights = lightData.visibleLights;
            if (lightData.additionalLightsCount > 0)
            {
                int additionalLightsCount = 0;
                for (int i = 0; i < lights.Length && additionalLightsCount < maxVisibleAdditionalLights; ++i)
                {
                    VisibleLight light = lights[i];
                    if (light.lightType != LightType.Directional)
                    {
                        LightCookie lightcookie;
                        InitializeLightConstants(lights, i, out m_AdditionalLightPositions[additionalLightsCount],
                            out m_AdditionalLightColors[additionalLightsCount],
                            out m_AdditionalLightAttenuations[additionalLightsCount],
                            out m_AdditionalLightSpotDirections[additionalLightsCount],
                            out lightcookie);
                        additionalLightsCount++;
                    }
                }

                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, new Vector4(lightData.maxPerObjectAdditionalLightsCount,
                    0.0f, 0.0f, 0.0f));
            }
            else
            {
                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, Vector4.zero);
            }

            cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsPosition, m_AdditionalLightPositions);
            cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsColor, m_AdditionalLightColors);
            cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsAttenuation, m_AdditionalLightAttenuations);
            cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsSpotDir, m_AdditionalLightSpotDirections);
        }
        
        void SetupPerObjectLightIndices(CullingResults cullResults, ref LightData lightData)
        {
            if (lightData.additionalLightsCount == 0)
                return;

            var visibleLights = lightData.visibleLights;
            var perObjectLightIndexMap = cullResults.GetLightIndexMap(Allocator.Temp);
            int directionalLightsCount = 0;
            int additionalLightsCount = 0;

            // Disable all directional lights from the perobject light indices
            // Pipeline handles them globally.
            for (int i = 0; i < visibleLights.Length; ++i)
            {
                if (additionalLightsCount >= LightweightRenderPipeline.maxVisibleAdditionalLights)
                    break;

                VisibleLight light = visibleLights[i];
                if (light.lightType == LightType.Directional)
                {
                    perObjectLightIndexMap[i] = -1;
                    ++directionalLightsCount;
                }
                else
                {
                    perObjectLightIndexMap[i] -= directionalLightsCount;
                    ++additionalLightsCount;
                }
            }

            // Disable all remaining lights we cannot fit into the global light buffer.
            for (int i = directionalLightsCount + additionalLightsCount; i < visibleLights.Length; ++i)
                perObjectLightIndexMap[i] = -1;

            cullResults.SetLightIndexMap(perObjectLightIndexMap);
            perObjectLightIndexMap.Dispose();
        }
    }
}
