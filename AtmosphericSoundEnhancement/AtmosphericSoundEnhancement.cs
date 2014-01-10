using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEngine;

namespace ASE
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AtmosphericSoundEnhancement : MonoBehaviour
    {
        // Acceptable approximation for Density to maximum audible frequency conversion.
        private const float MaxFreqCoef = 2500000;
        // All Kerbal-audible frequencies propagate through air with a Density above ~0.0089 kg/m^3.
        private const float MinFullSpectrumDensity = 0.0089f;
        // In stock KSP, hypersonic/re-entry effects aren't displayed in air with a Density above 0.1 kg/m^3.
        private const float MaxPlasmaActivationDensity = 0.10f;

        // Deadly Reentry compatibility.
        private const float DRStartThermal = 800; // m/s
        private const float DRFullThermal = 1150; // m/s

        private const float MinLowPassFreq = 10; // Hz
        private const float MaxLowPassFreq = 22000; // Hz

        private static KSP.IO.PluginConfiguration Config;

        // Remote Tech
        // Block communications when plasma is active.

        // States of sound medium
        public enum Soundscape
        {
            Unknown = 0,
            Interior,
            Vacuum,
            NormalFlight,
            BeforeShockwave,
            PositiveSlope,
            NegativeSlope,
            AfterShockwave,
        }
        public Soundscape CurrentState;
        public Soundscape LastState;
        public bool Paused;

        public List<ASEFilterPanel> AudioPanels;
        public int LastVesselPartCount;

        AerodynamicsFX AeroFX;

        float Density; // Realtime atmospheric Density.
        float LowerMachThreshold; // Lower Mach number for Mach audio and graphical effects.
        float UpperMachThreshold; // Upper Mach number for Mach graphical effects
        float MachNumber;//http://www.grc.nasa.gov/WWW/K-12/airplane/mach.html
        float MachAngle;//http://www.grc.nasa.gov/WWW/K-12/airplane/machang.html
        float CameraAngle;
        float NegativeSlopeWidthDeg; // Thickness of the shockwave.
        float ShockwaveEffectStrength;
        float MaxDistortion;
        float InteriorVolumeScale;
        float InteriorVolume;
        float InteriorMaxFreq; // Hz
        float MaxShipVolume;
        float CondensationEffectStrength;
        float MaxVacuumFreq; // Hz
        float MaxSupersonicFreq; // Hz

        public void Awake()
        {
            // Defaults for Configurable parameters.
            LowerMachThreshold = 0.80f;
            UpperMachThreshold = 1.20f;
            MaxDistortion = 0.95f;
            InteriorVolumeScale = 0.7f;
            InteriorMaxFreq = 300f;
            CondensationEffectStrength = 0.5f;
            MaxVacuumFreq = 0; // 150
            MaxSupersonicFreq = 0; // 300

            LoadConfig();

            // TODO:
            // Option for microphone fixed to craft.
            // Options for shockwave reverb/distortion levels.
            // Option for maintaining low-pass sound in vacuum.

            // Initialise dynamic variables.
            CurrentState = Soundscape.Unknown;
            LastState = Soundscape.Unknown;
            AudioPanels = new List<ASEFilterPanel>();
            LastVesselPartCount = 0;
            Density = 0;
            MachNumber = 0;
            MachAngle = 0;
            CameraAngle = 0;
            ShockwaveEffectStrength = 1f;
            NegativeSlopeWidthDeg = 24f;
            InteriorVolume = GameSettings.SHIP_VOLUME * InteriorVolumeScale;
            MaxShipVolume = GameSettings.SHIP_VOLUME;

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
            Paused = true;
            CurrentState = Soundscape.Unknown;
            foreach (ASEFilterPanel aPanel in AudioPanels)
                aPanel.SetKnobs(Knobs.all, -1);
        }

        public void OnUnPause()
        {
            Paused = false;
        }

        /// <summary>
        /// Called once per frame.
        /// </summary>
        public void Update()
        {
            if(Paused)
                return;
            UpdateAeroFX();
            UpdateAudioSources();
            if (AudioPanels.Count() == 0)
                return;

            LastState = CurrentState;
            if(CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal
            || CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA
            || CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Map)
                Interior();
            else
            {
                Density = (float)AtmoDataProvider.Get().GetDensity();
                if (Density <= 0)
                    Vacuum();
                else
                {
                    //volume = MaxShipVolume;
                    MachNumber = AtmoDataProvider.Get().GetMach();
                    if (MachNumber >= LowerMachThreshold)//transonic
                    {
                        MachAngle = 90f;
                        if (MachNumber >= 1f)
                        {
                            MachAngle = Mathf.Rad2Deg * (float)Math.Asin(1f / MachNumber);
                            ShockwaveEffectStrength = 1f;
                        }
                        else
                            ShockwaveEffectStrength = (MachNumber - LowerMachThreshold) / (1f - LowerMachThreshold);

                        NegativeSlopeWidthDeg = 5f + (15f / MachNumber);
                        float negativeSlopeEdgeDeg = MachAngle - NegativeSlopeWidthDeg;
                        float positiveSlopeWidthDeg = NegativeSlopeWidthDeg * (1f - ShockwaveEffectStrength);
                        float positiveSlopeEdgeDeg = Mathf.Max(0f, MachAngle + positiveSlopeWidthDeg);
                        CameraAngle = Vector3.Angle
                            (FlightGlobals.ActiveVessel.GetSrfVelocity().normalized
                            , (FlightGlobals.ActiveVessel.transform.position - FlightCamera.fetch.transform.position).normalized
                            );

                        if (CameraAngle >= negativeSlopeEdgeDeg && CameraAngle <= positiveSlopeEdgeDeg)//at shockwave
                            if (CameraAngle > MachAngle)
                                PositiveSlope(positiveSlopeEdgeDeg, positiveSlopeWidthDeg);
                            else
                                NegativeSlope(negativeSlopeEdgeDeg);
                        else
                            if (CameraAngle > positiveSlopeEdgeDeg)
                                BeforeShockwave();
                            else if (CameraAngle < negativeSlopeEdgeDeg)
                                AfterShockwave();
                    }
                    else
                        NormalFlight();
                } //end dense atmospheric conditions
            }//end external view
        }// end OnUpdate

        // By the second FixedUpdate, craft have been loaded. The first is fired before loading.
        private int _fixedUpdateCount = 0;
        public void FixedUpdate()
        {
            if (_fixedUpdateCount < 2)
                _fixedUpdateCount++;
            UpdateAeroFX();
        }

        public void LateUpdate()
        {
            UpdateAeroFX();
        }

        #region Persistence
        public void SaveConfig()
        {
            Debug.Log("ASE -- Saving settings: " + InteriorVolumeScale + " " + InteriorMaxFreq + " " + LowerMachThreshold + " " + UpperMachThreshold + " " + MaxDistortion + " " + CondensationEffectStrength + " " + MaxVacuumFreq + " " + MaxSupersonicFreq);
            Config.SetValue("InteriorVolumeScale", InteriorVolumeScale.ToString());
            Config.SetValue("InteriorMaxFreq", InteriorMaxFreq.ToString());
            Config.SetValue("LowerMachThreshold", LowerMachThreshold.ToString());
            Config.SetValue("UpperMachThreshold", UpperMachThreshold.ToString());
            Config.SetValue("MaxDistortion", MaxDistortion.ToString());
            Config.SetValue("CondensationEffectStrength", CondensationEffectStrength.ToString());
            Config.SetValue("MaxVacuumFreq", MaxVacuumFreq.ToString());
            Config.SetValue("MaxSupersonicFreq", MaxSupersonicFreq.ToString());
            Config.save();
        }

        public void LoadConfig()
        {
            Config = KSP.IO.PluginConfiguration.CreateForType<AtmosphericSoundEnhancement>();
            Config.load();
            InteriorVolumeScale = Config.GetValue<float>("InteriorVolumeScale", InteriorVolumeScale);
            InteriorMaxFreq = Config.GetValue<float>("InteriorMaxFreq", InteriorMaxFreq);
            LowerMachThreshold = Config.GetValue<float>("LowerMachThreshold", LowerMachThreshold);
            UpperMachThreshold = Config.GetValue<float>("UpperMachThreshold", UpperMachThreshold);
            MaxDistortion = Config.GetValue<float>("MaxDistortion", MaxDistortion);
            CondensationEffectStrength = Config.GetValue<float>( "CondensationEffectStrength"
                                                               , CondensationEffectStrength
                                                               );
            MaxVacuumFreq = Config.GetValue<float>("MaxVacuumFreq", MaxVacuumFreq);
            MaxSupersonicFreq = Config.GetValue<float>("MaxSupersonicFreq", MaxSupersonicFreq);
            Debug.Log("ASE -- Loaded settings: " + InteriorVolumeScale + " " + InteriorMaxFreq + " " + LowerMachThreshold + " " + UpperMachThreshold + " " + MaxDistortion + " " + CondensationEffectStrength + " " + MaxVacuumFreq + " " + MaxSupersonicFreq);
        }
        #endregion Persistence

        // Procedure methods to simplify Update() readability.

        #region Audio updates
        private void Interior()
        {
            CurrentState = Soundscape.Interior;
            if (CurrentState != LastState)
            {
                Debug.Log("ASE -- Switching to Interior");
                foreach (ASEFilterPanel aPanel in AudioPanels)
                {
                    aPanel.SetKnobs(Knobs.lowpass, InteriorMaxFreq);
                    aPanel.SetKnobs(Knobs.volume, InteriorVolume);
                    aPanel.SetKnobs(Knobs.distortion, -1);
                    aPanel.SetKnobs(Knobs.reverb, 0.05f);
                }
            }
        }

        private void Vacuum()
        {
            CurrentState = Soundscape.Vacuum;
            if (CurrentState != LastState)
            {
                Debug.Log("ASE -- Switching to Vacuum");
                if (MaxVacuumFreq < MinLowPassFreq)// Vacuum is set to silent.
                    foreach (ASEFilterPanel aPanel in AudioPanels)
                        aPanel.SetKnobs(Knobs.volume | Knobs.lowpass | Knobs.reverb | Knobs.distortion, -1);
                else// Vacuum is set to quiet.
                    foreach (ASEFilterPanel aPanel in AudioPanels)
                    {
                        aPanel.SetKnobs(Knobs.lowpass, MaxVacuumFreq);
                        aPanel.SetKnobs(Knobs.volume, MaxShipVolume);
                        aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1);
                    }
            }
        }

        /// <summary>
        /// Undisturbed air in front of the craft.
        /// </summary>
        private void BeforeShockwave()
        {
            CurrentState = Soundscape.BeforeShockwave;
            if (CurrentState != LastState)
                Debug.Log("ASE -- Switching to Before Shock");
            if (MaxSupersonicFreq < MinLowPassFreq)
            {
                // Silent ahead of the shockwave.
                float volume = MaxShipVolume * (1f - ShockwaveEffectStrength);
                foreach (ASEFilterPanel aPanel in AudioPanels)
                    aPanel.SetKnobs(Knobs.volume, volume);
                if (CurrentState != LastState)
                    foreach (ASEFilterPanel aPanel in AudioPanels)
                        aPanel.SetKnobs(Knobs.lowpass | Knobs.reverb | Knobs.distortion, -1);//effects off
            }
            else
            {
                // Low frequency ahead of the shockwave.
                foreach (ASEFilterPanel aPanel in AudioPanels)
                {
                    if (CurrentState != LastState)
                    {
                        aPanel.SetKnobs(Knobs.reverb | Knobs.distortion, -1);
                        aPanel.SetKnobs(Knobs.volume, MaxShipVolume);
                    }

                    float supersonicFreq = Mathf.Lerp(MaxSupersonicFreq, MaxLowPassFreq, 1f - ShockwaveEffectStrength);
                    float atmoFreq = GetAtmosphericAttenuation();
                    if (atmoFreq > 0)
                        aPanel.SetKnobs(Knobs.lowpass, Math.Min(atmoFreq, supersonicFreq));
                    else
                        aPanel.SetKnobs(Knobs.lowpass, supersonicFreq);
                }
            }
        }

        /// <summary>
        /// The leading edge of the shockwave: Translates to the first air the ear meets.
        /// </summary>
        /// <param name="positiveSlopeEdgeDeg"></param>
        /// <param name="positiveSlopeWidthDeg"></param>
        private void PositiveSlope(float positiveSlopeEdgeDeg, float positiveSlopeWidthDeg)
        {
            CurrentState = Soundscape.PositiveSlope;
            if (CurrentState != LastState)
                Debug.Log("ASE -- Switching to Rising Edge");
            float volume = Mathf.Lerp((1f - ShockwaveEffectStrength), 1f, (positiveSlopeEdgeDeg - CameraAngle) / positiveSlopeWidthDeg);
            float dynEffect = (positiveSlopeEdgeDeg - CameraAngle) / positiveSlopeWidthDeg * ShockwaveEffectStrength * MaxDistortion;
            foreach (ASEFilterPanel aPanel in AudioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, dynEffect);
                aPanel.SetKnobs(Knobs.lowpass, GetAtmosphericAttenuation());
            }
        }

        /// <summary>
        /// The trailing edge of the shockwave, closer to the inside of the shock cone.
        /// </summary>
        /// <param name="negativeSlopeEdgeDeg"></param>
        private void NegativeSlope(float negativeSlopeEdgeDeg)
        {
            CurrentState = Soundscape.NegativeSlope;
            if (CurrentState != LastState)
                Debug.Log("ASE -- Switching to Falling Edge");
            float dynEffect = ((CameraAngle - negativeSlopeEdgeDeg) / NegativeSlopeWidthDeg) * ShockwaveEffectStrength * MaxDistortion;
            foreach (ASEFilterPanel aPanel in AudioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, MaxShipVolume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, dynEffect);
                aPanel.SetKnobs(Knobs.lowpass, GetAtmosphericAttenuation());
            }
        }

        /// <summary>
        /// Inside the shock cone.
        /// </summary>
        private void AfterShockwave()
        {
            CurrentState = Soundscape.AfterShockwave;
            if (CurrentState != LastState)
                Debug.Log("ASE -- Switching to After Shock");
            foreach (ASEFilterPanel aPanel in AudioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, MaxShipVolume);
                aPanel.SetKnobs(Knobs.distortion, -1f);
                aPanel.SetKnobs(Knobs.reverb, 0.15f);
                aPanel.SetKnobs(Knobs.lowpass, GetAtmosphericAttenuation());
            }
        }

        private void NormalFlight()
        {
            CurrentState = Soundscape.NormalFlight;
            if (CurrentState != LastState)
                Debug.Log("ASE -- Switching to Normal Atmospheric Flight");
            foreach (ASEFilterPanel aPanel in AudioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, MaxShipVolume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1);
                aPanel.SetKnobs(Knobs.lowpass, GetAtmosphericAttenuation());
            }
        }

        /// <summary>
        /// Get the high-frequency attenuation due to thinning atmosphere.
        /// </summary>
        /// <param name="asePanel"></param>
        private float GetAtmosphericAttenuation()
        {
            if (Density <= MinFullSpectrumDensity)
                return Mathf.Max((float)Density * MaxFreqCoef, MaxVacuumFreq);// Get the highest frequency allowed.
            else
                return -1; // Thick atmosphere, normal sounds.
        }

        /// <summary>
        /// Update lists of noisy parts.  Update last vessel part count.
        /// </summary>
        private void UpdateAudioSources()
        {
            //TODO make conditional on GameEvent hooks if available
            //null reference paring.
            int apc = AudioPanels.Count;
            AudioPanels.RemoveAll(item => item.input == null);
            /*if (apc != AudioPanels.Count)
                Debug.Log("ASE -- removed " + (apc - AudioPanels.Count) + " panels.");*/

            // Wait until all craft have been loaded. The first fixed update is fired before this.
            if (_fixedUpdateCount >= 2)
                if (FlightGlobals.ActiveVessel.parts.Count != LastVesselPartCount || AudioPanels.Count() < 1)
                {
                    AudioPanels.Clear();
                    AudioSource[] audioSources = FindObjectsOfType(typeof(AudioSource)) as AudioSource[];
                    foreach (AudioSource s in audioSources)
                        if (s.gameObject.GetComponent<Part>() != null && s.clip != null)
                            AudioPanels.Add(new ASEFilterPanel(s.gameObject, s));
                    if (AeroFX != null)
                        AudioPanels.Add(new ASEFilterPanel(AeroFX.airspeedNoise.gameObject, AeroFX.airspeedNoise));
                    // Add relevant filters.
                    foreach (ASEFilterPanel aPanel in AudioPanels)
                        aPanel.AddKnobs(Knobs.distortion | Knobs.lowpass | Knobs.reverb);
                    LastVesselPartCount = FlightGlobals.ActiveVessel.parts.Count;
                }
        }
        #endregion Audio updates

        #region Graphical effects
        private void GetAeroFX()
        {
            GameObject fxLogicObject = GameObject.Find("FXLogic");
            if (fxLogicObject != null)
                AeroFX = fxLogicObject.GetComponent<AerodynamicsFX>();
        }

        private void UpdateAeroFX()
        {
            if (AeroFX == null)
                GetAeroFX();
            if (AeroFX != null)
            {
                float SurfaceVelocity = FlightGlobals.ActiveVessel.GetSrfVelocity().magnitude;

                if (MachNumber < LowerMachThreshold)// Subsonic.
                    AeroFX.fudge1 = 0; // Disable.
                else if (MachNumber >= LowerMachThreshold && MachNumber <= UpperMachThreshold)
                {
                    // Transonic.
                    AeroFX.airspeed = 400; // Lock speed within the range that it occurs so we can control it using one variable.
                    AeroFX.fudge1 = 3 + (1 - Mathf.Abs((MachNumber - 1) / (1 - LowerMachThreshold))) * CondensationEffectStrength;
                    AeroFX.state = 0; // Condensation.
                }
                else if (MachNumber > UpperMachThreshold && SurfaceVelocity < DRStartThermal)
                {
                    // Supersonic to hypersonic.
                    AeroFX.fudge1 = 0;
                    AeroFX.state = 0;
                }
                else if (SurfaceVelocity >= DRStartThermal && SurfaceVelocity < DRFullThermal)
                {
                    AeroFX.state = (SurfaceVelocity - DRStartThermal) / (DRFullThermal - DRStartThermal);
                    AeroFX.fudge1 = 3;
                }
                else if (SurfaceVelocity >= DRFullThermal)
                {
                    // Full re-entry speeds.
                    AeroFX.fudge1 = 3;
                    AeroFX.state = 1;
                }
                //else
                    //Debug.Log("ASE -- AeroFX: Invalid state!");
            }
        }
        #endregion Graphical effects
    }//end class
}//namespace
