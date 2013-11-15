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

        // States of sound medium
        public enum Soundscape
        {
            Unknown = 0,
            Paused,
            Interior,
            Vacuum,
            SparseAtmosphere,
            DenseAtmosphere,
            BeforeShockwave,
            RisingEdge,
            FallingEdge,
            AfterShockwave,
            Hypersonic
        }
        public Soundscape currentState;
        public Soundscape lastState;

        public List<ASEFilterPanel> audioPanels;
        public int lastVesselPartCount;

        PluginConfiguration config;

        float density;//realtime atmosphere density
        float lowerThreshold;//lower end to mach effects
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

        public void Awake()
        {
            // Configurable.
            lowerThreshold = 0.80f;
            shockwaveWidthDeg = 24f;
            maxDistortion = 0.95f;
            interiorVolumeScale = 0.7f;

            // TODO:
            // Option for microphone fixed to craft.
            // Options for shockwave reverb/distortion levels.
            // Option for maintaining low-pass sound in vacuum.

            // LoadConfig();

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


            /*cameraIsMicrophone = config.GetValue<bool>("Camera is microphone", true);
            soundInSpace = config.GetValue<bool>("Sound in space", false);
            muffledIvaSounds = config.GetValue<bool>("Muffled IVA sounds", true);
            muffledIvaMaxFreq = config.GetValue<float>("Max IVA frequency Hz", 600);*/
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


        public void Update()
        {//every frame
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
                            machAngle = Mathf.Rad2Deg * (float)Math.Asin(1f / machNumber);
                        else
                            shockwaveEffectStrength = (machNumber - lowerThreshold) / (1f - lowerThreshold);

                        shockwaveWidthDeg = 5f + (15f / machNumber);
                        float inbound = machAngle - shockwaveWidthDeg;
                        float outWidthDeg = shockwaveWidthDeg * (1f - shockwaveEffectStrength);
                        float outbound = machAngle + outWidthDeg;
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

                        if (machNumber > 5)
                            Hypersonic();
                    }
                    else
                        NormalFlight();
                } //end dense atmospheric conditions
            }//end external view
        }

        #region Persistence
        private void LoadConfig()
        {
            PluginConfiguration config = PluginConfiguration.CreateForType<AtmosphericSoundEnhancement>();
            config.load();
            lowerThreshold = config.GetValue<float>("Lower Mach Threshold", 0.80f);
            shockwaveWidthDeg = config.GetValue<float>("Shockwave Width Degrees", 24f);
            maxDistortion = config.GetValue<float>("Max Distortion", 0.95f);
            interiorVolumeScale = config.GetValue<float>("Interior Volume", 0.7f);
        }

        // Bootstrap the configuration file.
        private void SaveConfig()
        {
            if (config == null)
                config = PluginConfiguration.CreateForType<AtmosphericSoundEnhancement>();

            config["LowerMachThreshold"] = lowerThreshold;
            config["ShockwaveWidthDegrees"] = shockwaveWidthDeg;
            config["MaxDistortion"] = maxDistortion;
            config["InteriorVolume"] = interiorVolumeScale;
            config.save();
        }
        #endregion Persistence

        #region Audio updates
        /* Many simple, procedure methods to simplify Update() readability.
         * 
         */
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
            currentState = Soundscape.SparseAtmosphere;
            if (currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Sparse Atm");
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.SetKnobs(Knobs.volume | Knobs.lowpass | Knobs.distortion | Knobs.reverb, -1);//silence
            }
        }

        private void Hypersonic()
        {
            currentState = Soundscape.Hypersonic;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Hypersonic");

            // Fade-in re-entry effects from Mach 10 to full at Mach 25.
            // OR: Use same calculations as Deadly Reentry.
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
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb
                , (outbound - cameraAngle) / outWidthDeg * maxDistortion * shockwaveEffectStrength
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
                aPanel.SetKnobs(Knobs.distortion
                , (cameraAngle - inbound) / shockwaveWidthDeg * maxDistortion * shockwaveEffectStrength
                );
                aPanel.SetKnobs(Knobs.reverb, 0.15f);//testing light reverb
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

        private void AtmosphericAttenuation(ASEFilterPanel asePanel)
        {
            currentState = Soundscape.SparseAtmosphere;
            if (currentState != lastState)
                Debug.Log("ASE -- Switching to Falling Edge");
            if (density <= MinFullSpectrumDensity)
                asePanel.SetKnobs(Knobs.lowpass, (float)density * MaxFreqCoef);
        }

        /* Update lists of noisy parts.  Update last vessel part count.
         *
         */
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
                    if (s.gameObject.GetComponent<Part>() != null)
                        audioPanels.Add(new ASEFilterPanel(s.gameObject, s));
                }

                //add relevant filters
                foreach (ASEFilterPanel aPanel in audioPanels)
                    aPanel.AddKnobs(Knobs.filters);
                lastVesselPartCount = FlightGlobals.ActiveVessel.parts.Count;
            }
        }
        #endregion Audio updates
    }//end class
}//namespace