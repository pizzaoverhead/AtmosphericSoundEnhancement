using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class AtmosphericSoundEnhancement : MonoBehaviour
{
    // Approximate conversion factor for density to maximum frequency.
    // Provides acceptable accuracy vs. calculation based on the speed of sound.
    const float conversion = 2500000;
    int lastPartCount = 0;
    List<Part> audioParts = null;
    float maxSupersonicVolume = GameSettings.SHIP_VOLUME;
    bool isPaused = false;

    public void Start()
    {
        GameEvents.onGamePause.Add(new EventVoid.OnEvent(OnPause));
        GameEvents.onGameUnpause.Add(new EventVoid.OnEvent(OnUnPause));
    }

    public void OnPause()
    {
        isPaused = true;
        foreach (Part p in audioParts)
        {
            if (p.audio != null)
            {
                p.audio.volume = 0;
            }
        }
    }

    public void OnUnPause()
    {
        isPaused = false;
        foreach (Part p in audioParts)
        {
            if (p.audio != null)
            {
                p.audio.volume = GameSettings.SHIP_VOLUME;
            }
        }
    }

    public void OnDestroy()
    {
        GameEvents.onGamePause.Remove(new EventVoid.OnEvent(OnPause));
        GameEvents.onGameUnpause.Remove(new EventVoid.OnEvent(OnUnPause));
    }

    public void Update()
    {
        if (FlightGlobals.ActiveVessel.parts.Count != lastPartCount)
            UpdatePartsList();

        if (audioParts == null || isPaused)
            return;

        if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal ||
                CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA)
        {
            InternalSound();
        }
        else
        {
            foreach (Part p in audioParts)
            {
                VacuumFade(p);
                SonicBoom(p);
            }
        }
    }

    private void UpdatePartsList()
    {
        audioParts = FlightGlobals.ActiveVessel.parts.FindAll(p => p.audio != null);
        lastPartCount = FlightGlobals.ActiveVessel.parts.Count;
    }

    // Dull sounds internally.
    private void InternalSound()
    {
        foreach (Part p in audioParts)
        {
            if (p.audio != null)
            {
                AudioLowPassFilter filter = p.gameObject.GetComponent<AudioLowPassFilter>();
                if (filter == null)
                {
                    filter = p.gameObject.AddComponent<AudioLowPassFilter>();
                    filter.lowpassResonaceQ = 0f;
                }

                p.audio.volume = GameSettings.SHIP_VOLUME;
                filter.cutoffFrequency = 600;
                filter.enabled = true;
            }
        }
    }

    private void VacuumFade(Part p)
    {
        if (p.audio != null)
        {
            AudioLowPassFilter filter = p.gameObject.GetComponent<AudioLowPassFilter>();
            if (filter == null)
            {
                if (FlightGlobals.ActiveVessel.atmDensity > 0)
                {
                    filter = p.gameObject.AddComponent<AudioLowPassFilter>();
                    filter.lowpassResonaceQ = 0f;
                }
                else
                    return;
            }

            if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Flight ||
                CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.External ||
                CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Map)
            {
                if (FlightGlobals.ActiveVessel.atmDensity == 0)
                {
                    filter.enabled = false;
                    p.audio.volume = 0;
                }
                else
                {
                    if (FlightGlobals.ActiveVessel.atmDensity < 0.0089)
                    {
                        // AudioLowPassFilter filter has a max cuttof frequency of 22 kHz.
                        // At air densities of above ~0.0089 kg/m^3, the highest frequency heard is outside of this range.
                        filter.cutoffFrequency = (float)FlightGlobals.ActiveVessel.atmDensity * 2500000;
                        filter.enabled = true;
                    }
                    else
                        filter.enabled = false;

                    p.audio.volume = maxSupersonicVolume;
                }
            }
        }
    }

    private void SonicBoom(Part p)
    {
        Vessel v = FlightGlobals.ActiveVessel;
        float speedOfSound = SpeedOfSound(v);
        float temperature = v.flightIntegrator.getExternalTemperature();
        float surfaceVelocity = v.GetSrfVelocity().magnitude;
        bool supersonic = surfaceVelocity >= speedOfSound;

        // Get the angle between the shock cone and the camera. 0 = behind, 180 = in front.
        float cameraAngle = Vector3.Angle(v.CoM - v.GetSrfVelocity(), v.CoM - FlightCamera.fetch.transform.position);
        float shockWidthDeg = 20f;
        float effectStrength = 1f;

        if (p.audio != null) // Only operate on parts with sounds.
        {
            AudioDistortionFilter distortion = p.gameObject.GetComponent<AudioDistortionFilter>();
            AudioReverbFilter reverb = p.gameObject.GetComponent<AudioReverbFilter>();

            if (distortion == null)
            {
                if (supersonic && v.atmDensity > 0)
                    distortion = p.gameObject.AddComponent<AudioDistortionFilter>();
                else
                    return;
            }
            else if (v.atmDensity == 0)
            {
                p.audio.volume = 0;
                return;
            }

            if (reverb == null)
            {
                if (supersonic && v.atmDensity > 0)
                    reverb = p.gameObject.AddComponent<AudioReverbFilter>();
                else
                    return;
            }
            else if (v.atmDensity == 0)
            {
                p.audio.volume = 0;
                return;
            }

            if (supersonic)
            {
                float machAngle = Mathf.Rad2Deg * (float)Math.Asin(speedOfSound / surfaceVelocity);

                if (cameraAngle < (machAngle - shockWidthDeg))
                { // We are inside the cone.
                    distortion.enabled = false;
                    reverb.enabled = false;
                    p.audio.volume = GameSettings.SHIP_VOLUME;
                    maxSupersonicVolume = GameSettings.SHIP_VOLUME;
                }
                else if (cameraAngle >= (machAngle - shockWidthDeg) && cameraAngle <= (machAngle + shockWidthDeg))
                { // We are at the cone boundary.
                    distortion.enabled = true;
                    reverb.enabled = true;

                    if (cameraAngle < machAngle) // Boundary, inside
                    {
                        float fade = ((cameraAngle - (machAngle - shockWidthDeg)) / shockWidthDeg) * effectStrength;
                        distortion.distortionLevel = fade;
                        reverb.reverbLevel = fade;

                        p.audio.volume = GameSettings.SHIP_VOLUME;
                        maxSupersonicVolume = GameSettings.SHIP_VOLUME;
                    }
                    else if (cameraAngle == machAngle) // Boundary, on
                    {
                        distortion.distortionLevel = 1;
                        reverb.reverbLevel = 1;

                        p.audio.volume = GameSettings.SHIP_VOLUME;
                        maxSupersonicVolume = GameSettings.SHIP_VOLUME;
                    }
                    else if (cameraAngle > machAngle) // Boundary, outside
                    {
                        distortion.enabled = false;
                        reverb.enabled = false;

                        p.audio.volume = 0;
                        maxSupersonicVolume = 0;
                    }
                }
                else
                {
                    // We are outside the cone.
                    distortion.enabled = false;
                    reverb.enabled = false;

                    p.audio.volume = 0;
                }
            }
            else
            {
                // Subsonic.
                if (distortion != null)
                    distortion.enabled = false;
                if (reverb != null)
                    reverb.enabled = false;

                if (v.atmDensity > 0)
                    p.audio.volume = GameSettings.SHIP_VOLUME;
            }
        }
    }

    private float DistanceToCamera(Part part)
    {
        if (part.transform != null)
            return Vector3.Distance(part.transform.position, FlightCamera.fetch.transform.position);
        else return 0f;
    }

    private float SpeedOfSound(Vessel vessel)
    {
        float temperature = vessel.flightIntegrator.getExternalTemperature();
        return (float)(331.3 + 0.606 * temperature); // Fast approximation.
    }
}