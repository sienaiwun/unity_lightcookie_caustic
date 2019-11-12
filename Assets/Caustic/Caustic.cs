using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.LWRP;
[ExecuteAlways]
public class Caustic : MonoBehaviour
{
    public Texture[] caustics;
    [Range(1,3)]
    public float Boost;
    [Range(2,20)]
    public float FallOff;

    public Color CausticColor;

    private Light m_light;

    private LWRPAdditionalLightData m_lightData;

   
    void Start()
    {
        Boost = 1.0f;
        m_light = GetComponent<Light>();
        m_lightData = m_light.GetComponent<LWRPAdditionalLightData>();
        if (!m_lightData )
        {
            m_lightData = gameObject.AddComponent<LWRPAdditionalLightData>();
        }
        CausticColor = Color.blue;
    }
    
    void Update()
    {
        m_lightData.lightCookieFalloff = FallOff;
        m_lightData.lightCookieColor = CausticColor;
        if (m_light && caustics.Length>=1)
        {
            int index = ((int)(Time.frameCount/ Boost))%caustics.Length;
            m_light.cookie = caustics[index];
        }
    }

    private void OnDisable()
    {
        if (m_light)
        {
            m_light.cookie = null;
        }
    }
}
