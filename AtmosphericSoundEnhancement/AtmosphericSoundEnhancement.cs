using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEngine;
using KSP.IO;

namespace ASE
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AtmosphericSoundEnhancement : MonoBehaviour
    {
        // Acceptable approximation for density to maximum audible frequency conversion.
        private const float MaxFreqCoef = 2500000;
        // All Kerbal-audible frequencies propagate through air with a density above ~0.0089 kg/m^3.
        private const float MinFullSpectrumDensity = 0.0089f;
        // In stock KSP, hypersonic/re-entry effects aren't displayed in air with a density above 0.1 kg/m^3.
        private const float MaxPlasmaActivationDensity = 0.10f;

        // Deadly Reentry compatibility.
        private const float DRStartThermal = 800; // m/s
        private const float DRFullThermal = 1150; // m/s

        // Remote Tech
        // Block communications when plasma is active.

        // States of sound medium
        public enum Soundscape
        {
            Unknown = 0,
            Paused,
            Interior,
            Vacuum,
            DenseAtmosphere,
            BeforeShockwave,
            RisingEdge,
            FallingEdge,
            AfterShockwave,
        }
        public Soundscape currentState;
        public Soundscape lastState;

        public List<ASEFilterPanel> audioPanels;
        public int lastVesselPartCount;

        PluginConfiguration config;

        AerodynamicsFX aeroFX;

        float density;//realtime atmosphere density
        float lowerThreshold;//lower end to mach audio and graphical effects
        float upperThreshold;//upper end to mach graphical effects
        float machNumber;//http://www.grc.nasa.gov/WWW/K-12/airplane/mach.html
        float machAngle;//http://www.grc.nasa.gov/WWW/K-12/airplane/machang.html
        float cameraAngle;
        float shockwaveWidthDeg;//thickness of shockwave
        float shockwaveEffectStrength;
        float maxDistortion;
        float interiorVolumeScale;
        float interiorVolume;
        float maxShipVolume;
        float volume;//realtime, dynamic
        float plasmaEffectStrength;
        float condensationEffectStrength;

        public void Awake()
        {
            // Defaults for configurable parameters.
            lowerThreshold = 0.80f;
            upperThreshold = 1.20f;
            shockwaveWidthDeg = 24f;
            maxDistortion = 0.95f;
            interiorVolumeScale = 0.7f;
            condensationEffectStrength = 0.5f;

            LoadConfig();
            
            // TODO:
            // Option for microphone fixed to craft.
            // Options for shockwave reverb/distortion levels.
            // Option for maintaining low-pass sound in vacuum.

            // Initialise dynamic variables.
            currentState = Soundscape.Unknown;
            lastState = Soundscape.Unknown;
            audioPanels = new List<ASEFilterPanel>();
            lastVesselPartCount = 0;
            density = 0;
            machNumber = 0;
            machAngle = 0;
            cameraAngle = 0;
            shockwaveEffectStrength = 1f;
            interiorVolume = GameSettings.SHIP_VOLUME * interiorVolumeScale;
            maxShipVolume = GameSettings.SHIP_VOLUME;
            volume = GameSettings.SHIP_VOLUME;

            GetAeroFX();
        }

        public void Start()
        {
            GameEvents.onGamePause.Add(new EventVoid.OnEvent(OnPause));
            GameEvents.onGameUnpause.Add(new EventVoid.OnEvent(OnUnPause));
        }

        public void OnDestroy()
        {
            GameEvents.onGamePause.Remove(new EventVoid.OnEvent(OnPause));
            GameEvents.onGameUnpause.Remove(new EventVoid.OnEvent(OnUnPause));
        }

        public void OnPause()
        {
            currentState = Soundscape.Paused;
            foreach (ASEFilterPanel aPanel in audioPanels)
                aPanel.SetKnobs(Knobs.all, -1);
        }

        public void OnUnPause()
        {
            currentState = lastState;
        }

        /// <summary>
        /// Called once per frame.
        /// </summary>
        public void Update()
        {
            if (currentState == Soundscape.Paused) return;
            UpdateAudioSources();
            if (audioPanels.Count() == 0) return;//TODO weak, temporary

            lastState = currentState;
            if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal
                || CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA
                || CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Map)
                Interior();
            else
            {
                density = (float)AtmoDataProvider.Get().GetDensity();
                if (density <= 0)
                    Vacuum(); // No sounds.
                else
                {
                    volume = maxShipVolume;
                    machNumber = AtmoDataProvider.Get().GetMach();
                    if (machNumber >= lowerThreshold)
                    {
                        machAngle = 90f;
                        if (machNumber >= 1f)
                        {
                            machAngle = Mathf.Rad2Deg * (float)Math.Asin(1f / machNumber);
                            shockwaveEffectStrength = 1f;
                        }
                        else
                            shockwaveEffectStrength = (machNumber - lowerThreshold) / (1f - lowerThreshold);

                        shockwaveWidthDeg = 5f + (15f / machNumber);
                        float inbound = machAngle - shockwaveWidthDeg;
                        float outWidthDeg = shockwaveWidthDeg * (1f - shockwaveEffectStrength);
                        float outbound = Mathf.Max(0f, machAngle + outWidthDeg);
                        cameraAngle = Vector3.Angle
                            (FlightGlobals.ActiveVessel.GetSrfVelocity().normalized
                            , (FlightGlobals.ActiveVessel.transform.position - FlightCamera.fetch.transform.position).normalized
                            );

                        if (cameraAngle >= inbound && cameraAngle <= outbound)//at shockwave
                            if (cameraAngle > machAngle)
                                RisingEdge(outbound, outWidthDeg);
                            else
                                FallingEdge(inbound);
                        else
                        {
                            if (cameraAngle > outbound)
                                BeforeShockwave();
                            else if (cameraAngle < inbound)
                                AfterShockwave();
                        }
                    }
                    else
                        NormalFlight();
                } //end dense atmospheric conditions
            }//end external view

            UpdateAeroFX();
        }// end OnUpdate

        public void FixedUpdate()
        {
            UpdateAeroFX();
        }

        public void LateUpdate()
        {
            UpdateAeroFX();
        }

        #region Persistence
        private void LoadConfig()
        {
            if (!GameDatabase.Instance.ExistsConfigNode("AtmosphericSoundEnhancement"))
                Debug.Log("ASE -- No configuration file found.");
            else
            {
                foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes("AtmosphericSoundEnhancement"))
                {
                    Debug.Log("# Parsing node: " + node);
                    if (node.HasValue("interiorVolumeScale"))
                    {
                        float interiorVol = interiorVolumeScale;
                        if (float.TryParse(node.GetValue("interiorVolumeScale"), out interiorVol))
                            interiorVolumeScale = interiorVol;
                    }
                    if (node.HasValue("lowerMachThreshold"))
                    {
                        float lowerMachThreshold = lowerThreshold;
                        if (float.TryParse(node.GetValue("lowerMachThreshold"), out lowerMachThreshold))
                            lowerThreshold = lowerMachThreshold;
                    }
                    if (node.HasValue("upperMachThreshold"))
                    {
                        float upperMachThreshold = upperThreshold;
                        if (float.TryParse(node.GetValue("upperMachThreshold"), out upperMachThreshold))
                            upperThreshold = upperMachThreshold;
                    }
                    if (node.HasValue("shockwaveWidthDeg"))
                    {
                        float shockWidthDeg = shockwaveWidthDeg;
                        if (float.TryParse(node.GetValue("shockwaveWidthDeg"), out shockWidthDeg))
                            shockwaveWidthDeg = shockWidthDeg;
                    }
                    if (node.HasValue("maxDistortion"))
                    {
                        float maxDist = maxDistortion;
                        if (float.TryParse(node.GetValue("maxDistortion"), out maxDist))
                            maxDistortion = maxDist;
                    }
                    if (node.HasValue("interiorVolumeScale"))
                    {
                        float interiorVol = interiorVolumeScale;
                        if (float.TryParse(node.GetValue("interiorVolumeScale"), out interiorVol))
                            interiorVolumeScale = interiorVol;
                    }
                    if (node.HasValue("condensationEffectStrength"))
                    {
                        float condStrength = condensationEffectStrength;
                        if (float.TryParse(node.GetValue("condensationEffectStrength"), out condStrength))
                            condensationEffectStrength = condStrength;
                    }
                }
            }
        }
        #endregion Persistence

        // Many simple, procedure methods to simplify Update() readability.
        #region Audio updates
        private void Interior()
        {
            currentState = Soundscape.Interior;
            if (currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Interior");
                foreach (ASEFilterPanel aPanel in audioPanels)
                {
                    aPanel.SetKnobs(Knobs.lowpass, 600);
                    aPanel.SetKnobs(Knobs.volume, interiorVolume);
                    aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1);
                }
            }
        }

        private void Vacuum()
        {
            currentState = Soundscape.Vacuum;
            if (currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Vacuum");
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.SetKnobs(Knobs.volume | Knobs.lowpass | Knobs.distortion | Knobs.reverb, -1);//silence
            }
        }

        /// <summary>
        /// Undisturbed air in front of the craft.
        /// </summary>
        private void BeforeShockwave()
        {
            currentState = Soundscape.BeforeShockwave;
            volume *= (1f - shockwaveEffectStrength);
            foreach (ASEFilterPanel aPanel in audioPanels)
                aPanel.SetKnobs(Knobs.volume, volume);
            if (currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Before Shock");
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.SetKnobs(Knobs.lowpass | Knobs.distortion | Knobs.reverb, -1);//silence
            }
        }

        /// <summary>
        /// The leading edge of the shock cone: The first air the craft meets.
        /// </summary>
        /// <param name="outbound"></param>
        /// <param name="outWidthDeg"></param>
        private void RisingEdge(float outbound, float outWidthDeg)
        {
            currentState = Soundscape.RisingEdge;
            volume *= Mathf.Lerp((1f - shockwaveEffectStrength), 1f, (outbound - cameraAngle) / outWidthDeg);

            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Rising Edge");
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, 
                    (outbound - cameraAngle) / outWidthDeg * maxDistortion * shockwaveEffectStrength
                );
                AtmosphericAttenuation(aPanel);
            }
        }

        /// <summary>
        /// The trailing edge of the shock cone, behind the shock wave.
        /// </summary>
        /// <param name="inbound"></param>
        private void FallingEdge(float inbound)
        {
            currentState = Soundscape.FallingEdge;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Falling Edge");
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb,
                    (cameraAngle - inbound) / shockwaveWidthDeg * maxDistortion * shockwaveEffectStrength
                );
                //aPanel.SetKnobs(Knobs.reverb, 0.15f);//testing light reverb
                AtmosphericAttenuation(aPanel);
            }
        }

        /// <summary>
        /// Inside the shock wave.
        /// </summary>
        private void AfterShockwave()
        {
            currentState = Soundscape.AfterShockwave;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to After Shock");
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1f);
                AtmosphericAttenuation(aPanel);
            }
        }

        private void NormalFlight()
        {
            currentState = Soundscape.DenseAtmosphere;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Normal Atm Flight");
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1);
                AtmosphericAttenuation(aPanel);
            }
        }

        /// <summary>
        /// Set the high-frequency attenuation due to thinning atmosphere.
        /// </summary>
        /// <param name="asePanel"></param>
        private void AtmosphericAttenuation(ASEFilterPanel asePanel)
        {
            if (density <= MinFullSpectrumDensity)
                asePanel.SetKnobs(Knobs.lowpass, (float)density * MaxFreqCoef);
            else
                asePanel.SetKnobs(Knobs.lowpass, -1);
        }

        /// <summary>
        /// Update lists of noisy parts.  Update last vessel part count.
        /// </summary>
        private void UpdateAudioSources()
        {
            //TODO make conditional on GameEvent hooks if available
            //null reference paring.
            audioPanels.RemoveAll(item => item.gameObj == null);
            //TODO skip if in space (flatten state hierarchy slightly)
            if (FlightGlobals.ActiveVessel.parts.Count != lastVesselPartCount || audioPanels.Count() < 1)
            {
                audioPanels.Clear();
                AudioSource[] audioSources = FindObjectsOfType(typeof(AudioSource)) as AudioSource[];
                foreach (AudioSource s in audioSources)
                {
                    if (s.gameObject.GetComponent<Part>() != null && s.clip != null)
                        audioPanels.Add(new ASEFilterPanel(s.gameObject, s));
                }

                GetAeroFX();
                if (aeroFX != null)
                {
                    audioPanels.Add(new ASEFilterPanel(aeroFX.airspeedNoise.gameObject, aeroFX.airspeedNoise));
                }

                //add relevant filters
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.AddKnobs(Knobs.filters);
                lastVesselPartCount = FlightGlobals.ActiveVessel.parts.Count;
            }
        }
        #endregion Audio updates

        #region Graphical effects
        private void GetAeroFX()
        {
            GameObject fxLogicObject = GameObject.Find("FXLogic");
            if (fxLogicObject != null)
            {
                aeroFX = fxLogicObject.GetComponent<AerodynamicsFX>();
            }
        }

        private void UpdateAeroFX()
        {
            if (aeroFX == null)
                GetAeroFX();
            if (aeroFX != null)
            {
                if (machNumber < lowerThreshold)
                {
                    // Subsonic.
                    aeroFX.fudge1 = 0; // Disable.
                }
                else if (machNumber >= lowerThreshold && machNumber <= upperThreshold)
                {
                    // Transonic.
                    aeroFX.airspeed = 400; // Lock speed within the range that it occurs so we can control it using one variable.
                    aeroFX.fudge1 = 3 + (1 - Mathf.Abs((machNumber - 1) / (1 - lowerThreshold))) * condensationEffectStrength;
                    aeroFX.state = 0; // Condensation.
                }
                else if (machNumber > upperThreshold && machNumber < 3)
                {
                    // Supersonic.
                    aeroFX.fudge1 = 0;
                    aeroFX.state = 0;
                }
                else if (aeroFX.velocity.magnitude < DRStartThermal) // approximate speed where shockwaves begin visibly glowing
                {
                    aeroFX.fudge1 = 0;
                    aeroFX.state = 0;
                }
                else if (aeroFX.velocity.magnitude >= DRFullThermal)
                {
                    aeroFX.fudge1 = 3;
                    aeroFX.state = 1;
                }
                else
                {
                    aeroFX.state = (aeroFX.velocity.magnitude - DRStartThermal) / (DRFullThermal - DRStartThermal);
                    aeroFX.fudge1 = 3;
                }
            }
            //Debug.Log("#FX After: " + aeroFX.fudge1 + " " + aeroFX.state);
        }
        #endregion Graphical effects
    }//end class
}//namespace