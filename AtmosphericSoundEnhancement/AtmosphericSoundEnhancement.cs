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

        ConfigNode config;
        string configSavePath = "GameData/AtmosphericSoundEnhancement/settings.cfg";

        // Remote Tech
        // Block communications when plasma is active.

        // States of sound medium
        public enum Soundscape
        {
            Unknown = 0,
            Paused,
            Interior,
            Vacuum,
            NormalFlight,
            BeforeShockwave,
            PositiveSlope,
            NegativeSlope,
            AfterShockwave,
        }
        public Soundscape currentState;
        public Soundscape lastState;

        public List<ASEFilterPanel> audioPanels;
        public int lastVesselPartCount;

        AerodynamicsFX aeroFX;

        float density; // Realtime atmospheric density.
        float lowerThreshold; // Lower Mach number for Mach audio and graphical effects.
        float upperThreshold; // Upper Mach number for Mach graphical effects
        float machNumber;//http://www.grc.nasa.gov/WWW/K-12/airplane/mach.html
        float machAngle;//http://www.grc.nasa.gov/WWW/K-12/airplane/machang.html
        float cameraAngle;
        float negativeSlopeWidthDeg; // Thickness of the shockwave.
        float shockwaveEffectStrength;
        float maxDistortion;
        float interiorVolumeScale;
        float interiorVolume;
        float maxShipVolume;
        float condensationEffectStrength;

        public void Awake()
        {
            // Defaults for configurable parameters.
            lowerThreshold = 0.80f;
            upperThreshold = 1.20f;
            negativeSlopeWidthDeg = 24f;
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
            SaveConfig();
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
                    //volume = maxShipVolume;
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

                        negativeSlopeWidthDeg = 5f + (15f / machNumber);
                        float negativeSlopeEdgeDeg = machAngle - negativeSlopeWidthDeg;
                        float positiveSlopeWidthDeg = negativeSlopeWidthDeg * (1f - shockwaveEffectStrength);
                        float positiveSlopeEdgeDeg = Mathf.Max(0f, machAngle + positiveSlopeWidthDeg);
                        cameraAngle = Vector3.Angle
                            (FlightGlobals.ActiveVessel.GetSrfVelocity().normalized
                            , (FlightGlobals.ActiveVessel.transform.position - FlightCamera.fetch.transform.position).normalized
                            );

                        if (cameraAngle >= negativeSlopeEdgeDeg && cameraAngle <= positiveSlopeEdgeDeg)//at shockwave
                            if (cameraAngle > machAngle)
                                PositiveSlope(positiveSlopeEdgeDeg, positiveSlopeWidthDeg);
                            else
                                NegativeSlope(negativeSlopeEdgeDeg);
                        else
                        {
                            if (cameraAngle > positiveSlopeEdgeDeg)
                                BeforeShockwave();
                            else if (cameraAngle < negativeSlopeEdgeDeg)
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
        public void SaveConfig()
        {
            Debug.Log("ASE -- Saving...");

            UpdateConfigValue("interiorVolumeScale", interiorVolumeScale);
            UpdateConfigValue("lowerMachThreshold", lowerThreshold);
            UpdateConfigValue("upperMachThreshold", upperThreshold);
            UpdateConfigValue("negativeSlopeWidthDeg", negativeSlopeWidthDeg);
            UpdateConfigValue("maxDistortion", maxDistortion);
            UpdateConfigValue("condensationEffectStrength", condensationEffectStrength);

            config.Save(configSavePath);
        }

        private void UpdateConfigValue(string name, object value)
        {
            if (config == null)
            {
                config = ConfigNode.Load(configSavePath);
                if (config == null)
                    config = new ConfigNode();
            }

            if (config.HasValue(name))
                config.RemoveValue(name);
            config.AddValue(name, value);
        }

        public void LoadConfig()
        {
            Debug.Log("ASE -- Loading...");
            config = ConfigNode.Load(configSavePath);
            if (config == null)
            {
                Debug.Log("ASE -- No config file present.");
                return;
            }

            if (config.HasValue("interiorVolumeScale"))
            {
                Debug.Log("ASE -- Found interiorVolumeScale");
                float interiorVol = interiorVolumeScale;
                if (float.TryParse(config.GetValue("interiorVolumeScale"), out interiorVol))
                    interiorVolumeScale = interiorVol;
            }
            if (config.HasValue("lowerMachThreshold"))
            {
                Debug.Log("ASE -- Found lowerMachThreshold");
                float lowerMachThreshold = lowerThreshold;
                if (float.TryParse(config.GetValue("lowerMachThreshold"), out lowerMachThreshold))
                    lowerThreshold = lowerMachThreshold;
            }
            if (config.HasValue("upperMachThreshold"))
            {
                Debug.Log("ASE -- Found upperMachThreshold");
                float upperMachThreshold = upperThreshold;
                if (float.TryParse(config.GetValue("upperMachThreshold"), out upperMachThreshold))
                    upperThreshold = upperMachThreshold;
            }
            if (config.HasValue("trailingEdgeWidthDeg"))
            {
                Debug.Log("ASE -- Found negativeSlopeWidthDeg");
                float negativeWidthDeg = negativeSlopeWidthDeg;
                if (float.TryParse(config.GetValue("negativeSlopeWidthDeg"), out negativeWidthDeg))
                    negativeSlopeWidthDeg = negativeWidthDeg;
            }
            if (config.HasValue("maxDistortion"))
            {
                Debug.Log("ASE -- Found maxDistortion");
                float maxDist = maxDistortion;
                if (float.TryParse(config.GetValue("maxDistortion"), out maxDist))
                    maxDistortion = maxDist;
            }
            if (config.HasValue("condensationEffectStrength"))
            {
                Debug.Log("ASE -- Found condensationEffectStrength");
                float condStrength = condensationEffectStrength;
                if (float.TryParse(config.GetValue("condensationEffectStrength"), out condStrength))
                    condensationEffectStrength = condStrength;
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
                    aPanel.SetKnobs(Knobs.distortion, -1);
                    aPanel.SetKnobs(Knobs.reverb, 0.05f);
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
                    aPanel.SetKnobs(Knobs.volume | Knobs.lowpass | Knobs.reverb | Knobs.distortion, -1);//silence
            }
        }

        /// <summary>
        /// Undisturbed air in front of the craft.
        /// </summary>
        private void BeforeShockwave()
        {
            currentState = Soundscape.BeforeShockwave;
            float volume = maxShipVolume * (1f - shockwaveEffectStrength);
            foreach (ASEFilterPanel aPanel in audioPanels)
                aPanel.SetKnobs(Knobs.volume, volume);
            if (currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Before Shock");
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.SetKnobs(Knobs.lowpass | Knobs.reverb | Knobs.distortion, -1);//effects off
            }
        }

        /// <summary>
        /// The leading edge of the shock cone: The first air the craft meets.
        /// </summary>
        /// <param name="positiveSlopeEdgeDeg"></param>
        /// <param name="positiveSlopeWidthDeg"></param>
        private void PositiveSlope(float positiveSlopeEdgeDeg, float positiveSlopeWidthDeg)
        {
            currentState = Soundscape.PositiveSlope;
            float volume = Mathf.Lerp((1f - shockwaveEffectStrength), 1f, (positiveSlopeEdgeDeg - cameraAngle) / positiveSlopeWidthDeg);
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Rising Edge");
            float dynEffect = (positiveSlopeEdgeDeg - cameraAngle) / positiveSlopeWidthDeg * shockwaveEffectStrength * maxDistortion ;
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, dynEffect);
                AtmosphericAttenuation(aPanel);
            }
        }

        /// <summary>
        /// The trailing edge of the shock cone, behind the shock wave.
        /// </summary>
        /// <param name="negativeSlopeEdgeDeg"></param>
        private void NegativeSlope(float negativeSlopeEdgeDeg)
        {
            currentState = Soundscape.NegativeSlope;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Falling Edge");
            float dynEffect = ((cameraAngle - negativeSlopeEdgeDeg) / negativeSlopeWidthDeg) * shockwaveEffectStrength * maxDistortion;
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, maxShipVolume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, dynEffect);
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
                aPanel.SetKnobs(Knobs.volume, maxShipVolume);
                aPanel.SetKnobs(Knobs.distortion, -1f);
                aPanel.SetKnobs(Knobs.reverb, 0.15f);
                AtmosphericAttenuation(aPanel);
            }
        }

        private void NormalFlight()
        {
            currentState = Soundscape.NormalFlight;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Normal Atm Flight");
            foreach (ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, maxShipVolume);
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
            audioPanels.RemoveAll(item => item.input == null);
            //TODO skip if in space (flatten state hierarchy slightly)
            if (FlightGlobals.ActiveVessel.parts.Count != lastVesselPartCount || audioPanels.Count() < 1)
            {
                audioPanels.Clear();
                AudioSource[] audioSources = FindObjectsOfType(typeof(AudioSource)) as AudioSource[];
                foreach (AudioSource s in audioSources)
                {
                    if (s.gameObject.GetComponent<Part>() != null && s.clip != null)
                    {
                        Debug.Log("ASE -- Found AudioSource on Part: " + s.gameObject.GetComponent<Part>());
                        audioPanels.Add(new ASEFilterPanel(s.gameObject, s));
                    }
                }

                GetAeroFX();
                if (aeroFX != null)
                {
                    audioPanels.Add(new ASEFilterPanel(aeroFX.airspeedNoise.gameObject, aeroFX.airspeedNoise));
                }

                //add relevant filters
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.AddKnobs(Knobs.distortion | Knobs.lowpass | Knobs.reverb);
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