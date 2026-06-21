using UnityEngine;
using UnityEngine.Rendering;

// Drives the directional light in the custom level prefab based on local system time.
// Attached to the client-side CustomLevel_Client GameObject at runtime.
// Chat commands: /timeset HHMM  |  /timeset auto
public class LevelDayNightCycle : MonoBehaviour
{
    // Set by /timeset command; null means follow system clock
    public static float? ManualHour = null;
    public static LevelDayNightCycle Instance { get; private set; }

    private Light _sun;
    private float _nextUpdate;

    private static readonly Color DayColor     = new Color(0.992f, 0.957f, 0.863f);
    private static readonly Color HorizonColor = new Color(1f, 0.55f, 0.25f);
    private static readonly Color MoonColor    = new Color(0.6f, 0.7f, 1f);
    private static readonly Color AmbientDay   = new Color(0.55f, 0.53f, 0.48f);
    private static readonly Color AmbientNight = new Color(0.35f, 0.40f, 0.55f);

    void Awake()
    {
        Instance = this;
        _sun = GetComponentInChildren<Light>();
    }

    // Resets the timer so Update applies the new ManualHour on the very next frame
    public void ForceUpdate() => _nextUpdate = 0f;

    void Update()
    {
        if (_sun == null) return;

        // The DayNightCycle mod (MyPuckMod) creates its own directional light. Keeping shadows
        // off on our prefab light every frame prevents double-shadow artifacts on sticks/pucks.
        _sun.shadows = LightShadows.None;

        if (Time.unscaledTime < _nextUpdate) return;
        _nextUpdate = Time.unscaledTime + 30f;

        float hour = ManualHour ?? (float)System.DateTime.Now.TimeOfDay.TotalHours;

        // sinH > 0 = daytime, sinH < 0 = night; peaks at solar noon (12:00)
        float sinH = Mathf.Sin((hour - 6f) / 12f * Mathf.PI);
        float dayT = Mathf.Clamp01((sinH + 0.15f) / 0.45f);

        if (sinH >= 0f)
        {
            float angle = hour / 24f * 360f - 90f;
            _sun.transform.rotation = Quaternion.Euler(angle, 170f, 0f);
            float t = Mathf.Clamp01(sinH / 0.35f);
            _sun.color = Color.Lerp(HorizonColor, DayColor, t);
            _sun.intensity = Mathf.Lerp(0.15f, 0.8f, Mathf.Clamp01(sinH / 0.3f));
        }
        else
        {
            // Offset by 12h so the moon arc starts at dusk and ends at dawn
            float nightHour = Mathf.Repeat(hour + 12f, 24f);
            float angle = nightHour / 24f * 360f - 90f;
            _sun.transform.rotation = Quaternion.Euler(angle, 200f, 0f);
            _sun.color = MoonColor;
            _sun.intensity = Mathf.Lerp(0.4f, 0.6f, Mathf.Clamp01(-sinH / 0.3f));
        }

        RenderSettings.ambientLight = Color.Lerp(AmbientNight, AmbientDay, dayT);
    }
}

// Prevents two visual artifacts caused by the DayNightCycle mod overriding terrain settings
// every 0.25s with a low shadowBias directional light:
//   - Basemap transition: bright patch visible around the camera when basemapDistance is low
//   - Self-shadowing: rectangular black patches on flat terrain from low shadowBias
// LateUpdate runs after the mod's update loop so our values always take priority.
public class TerrainBasemapEnforcer : MonoBehaviour
{
    void LateUpdate()
    {
        foreach (Terrain t in Terrain.activeTerrains)
        {
            t.basemapDistance = 10000f;
            t.shadowCastingMode = ShadowCastingMode.Off;
        }
    }
}
