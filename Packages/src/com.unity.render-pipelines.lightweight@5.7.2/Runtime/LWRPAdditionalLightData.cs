namespace UnityEngine.Rendering.LWRP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public class LWRPAdditionalLightData : MonoBehaviour
    {
        [Tooltip("Controls the usage of pipeline settings.")]
        [SerializeField] bool m_UsePipelineSettings = true;

        [Tooltip("The fall off distance of light cookies")]
        [SerializeField] float m_lightCookieFalloff = 1.0f;

        [Tooltip("The color of cookie")]
        [SerializeField] Color m_lightCookieColor = Color.white;



        public bool usePipelineSettings
        {
            get { return m_UsePipelineSettings; }
            set { m_UsePipelineSettings = value; }
        }

        public float lightCookieFalloff
        {
            get { return m_lightCookieFalloff; }
            set { m_lightCookieFalloff = value; }
        }

        public Color lightCookieColor
        {
            get { return m_lightCookieColor; }
            set { m_lightCookieColor = value; }
        }
    }
}
