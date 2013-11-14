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
        //Acceptable approximation for density to maximum audible frequency conversion.
        private const float MaxFreqCoef = 2500000;
        //Kerbal-audible frequencies do not propagate through air density below ~0.0089 kg/m^3
        private const float MinAuralDensity = 0.0089f;

        //states of sound medium
        public enum Soundscape
        { Unknown=0
        , Paused
        , Interior
        , SparseAtmosphere
        , DenseAtmosphere
        , BeforeShockwave
        , RisingEdge
        , FallingEdge
        , AfterShockwave
        }
        public Soundscape currentState;
        public Soundscape lastState;

        public List<ASEFilterPanel> audioPanels;
        public int lastVesselPartCount;

        float density;//realtime atmosphere density
        float lowerThreshold;//lower end to mach effects
        float machNumber;//http://www.grc.nasa.gov/WWW/K-12/airplane/mach.html
        float machAngle;//http://www.grc.nasa.gov/WWW/K-12/airplane/machang.html
        float cameraAngle;
        float shockwaveWidthDeg;//thickness of shockwave
        float shockwaveEffectStrength;
        float maxDistortion;
        float interiorVolume;
        float maxShipVolume;
        float volume;//realtime, dynamic

        public void Awake()
        {
            currentState = Soundscape.Unknown;
            lastState = Soundscape.Unknown;
            audioPanels = new List<ASEFilterPanel>();
            lastVesselPartCount = 0;
            density = 0;
            lowerThreshold = 0.80f;
            machNumber = 0;
            machAngle = 0;
            cameraAngle = 0;
            shockwaveWidthDeg = 24f;
            shockwaveEffectStrength = 1f;
            maxDistortion = 0.95f;
            interiorVolume = GameSettings.SHIP_VOLUME * 0.7f;
            maxShipVolume = GameSettings.SHIP_VOLUME;
            volume = GameSettings.SHIP_VOLUME;
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
                aPanel.SetKnobs(Knobs.volume, 0);
        }
        public void OnUnPause()
        {
            currentState = lastState;
        }


        public void Update()
        {//every frame
            if(currentState == Soundscape.Paused) return;
            UpdateAudioSources();
            if(audioPanels.Count() == 0) return;//TODO weak, temporary

            lastState = currentState;
            if(CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal
            || CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA)
                Interior();
            else
            {
                density = (float)AtmoDataProvider.Get().GetDensity();
                if(density < MinAuralDensity)
                    Sparse();//vacuum or air too thin for audible sound
                else
                {//dense air
                    volume = Mathf.Clamp((float)(density / MinAuralDensity), 0f, maxShipVolume);
                    machNumber = AtmoDataProvider.Get().GetMach();
                    if(machNumber >= lowerThreshold)
                    {
                        machAngle = 90f;
                        if(machNumber >= 1f)
                            machAngle = Mathf.Rad2Deg * (float)Math.Asin(1f/machNumber);
                        else
                            shockwaveEffectStrength = (machNumber - lowerThreshold)/(1f - lowerThreshold);
                        
                        shockwaveWidthDeg = 5f + (15f/machNumber);
                        float inbound = machAngle - shockwaveWidthDeg;
                        float outWidthDeg = shockwaveWidthDeg * (1f - shockwaveEffectStrength);
                        float outbound = machAngle + outWidthDeg;
                        cameraAngle = Vector3.Angle
                            ( FlightGlobals.ActiveVessel.GetSrfVelocity().normalized
                            , (FlightGlobals.ActiveVessel.transform.position - FlightCamera.fetch.transform.position).normalized
                            );

                        if(cameraAngle >= inbound && cameraAngle <= outbound)//at shockwave
                            if(cameraAngle > machAngle)
                                RisingEdge(outbound, outWidthDeg);
                            else
                                FallingEdge(inbound);
                        else
                        {
                            if(cameraAngle > outbound)
                                BeforeShockwave();
                            else if(cameraAngle < inbound)
                                AfterShockwave();
                        }
                    }
                    else
                        NormalFlight();
                }//end dense air
            }//end outside
        }

        /* many simple, procedure methods to simplify Update() readability
         */
        private void Interior()
        {
            currentState = Soundscape.Interior;
            if(currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Interior");
                foreach(ASEFilterPanel aPanel in audioPanels)
                {
                    aPanel.SetKnobs(Knobs.lowpass, 600);
                    aPanel.SetKnobs(Knobs.volume, interiorVolume);
                    aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1);
                }
            }
        }
        private void Sparse()
        {
            currentState = Soundscape.SparseAtmosphere;
            if(currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Sparse Atm");
                foreach(ASEFilterPanel aPanel in audioPanels)
                    aPanel.SetKnobs(Knobs.volume | Knobs.lowpass | Knobs.distortion | Knobs.reverb, -1);//silence
            }
        }
        private void BeforeShockwave()
        {
            currentState = Soundscape.BeforeShockwave;
            volume *= (1f - shockwaveEffectStrength);
            foreach(ASEFilterPanel aPanel in audioPanels)
                aPanel.SetKnobs(Knobs.volume, volume);
            if(currentState != lastState)
            {
                Debug.Log("ASE -- Switching to Before Shock");
                foreach(ASEFilterPanel aPanel in audioPanels)
                  aPanel.SetKnobs(Knobs.lowpass | Knobs.distortion | Knobs.reverb, -1);//silence
            }
        }
        private void RisingEdge(float outbound, float outWidthDeg)
        {
            currentState = Soundscape.RisingEdge;
            volume *= Mathf.Lerp((1f - shockwaveEffectStrength), 1f, (outbound - cameraAngle) / outWidthDeg);

            if(currentState != lastState)
                Debug.Log("ASE -- Switching to Rising Edge");
            foreach(ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb
                , (outbound - cameraAngle) / outWidthDeg * maxDistortion * shockwaveEffectStrength
                );
                aPanel.SetKnobs(Knobs.reverb, -1f);
                aPanel.SetKnobs(Knobs.lowpass, (float)density * MaxFreqCoef);
            }
        }
        private void FallingEdge(float inbound)
        {
            currentState = Soundscape.FallingEdge;
            if(currentState != lastState)
                Debug.Log("ASE -- Switching to Falling Edge");
            foreach(ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion
                , (cameraAngle - inbound) / shockwaveWidthDeg * maxDistortion * shockwaveEffectStrength
                );
                aPanel.SetKnobs(Knobs.reverb, 0.15f);//testing light reverb
                aPanel.SetKnobs(Knobs.lowpass, (float)density * MaxFreqCoef);
            }
        }
        private void AfterShockwave()
        {
            currentState = Soundscape.AfterShockwave;
            if(currentState != lastState)
                Debug.Log("ASE -- Switching to After Shock");
            foreach(ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion, 0f);
                aPanel.SetKnobs(Knobs.reverb, 0.1f);//testing light reverb
                aPanel.SetKnobs(Knobs.lowpass, (float)density * MaxFreqCoef);
            }
        }
        private void NormalFlight()
        {
            currentState = Soundscape.DenseAtmosphere;
            if(currentState != lastState)
                Debug.Log("ASE -- Switching to Normal Atm Flight");
            foreach(ASEFilterPanel aPanel in audioPanels)
            {
                aPanel.SetKnobs(Knobs.volume, volume);
                aPanel.SetKnobs(Knobs.distortion | Knobs.reverb, -1);
                aPanel.SetKnobs(Knobs.lowpass, (float)density * MaxFreqCoef);
            }
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
            if(FlightGlobals.ActiveVessel.parts.Count != lastVesselPartCount || audioPanels.Count() < 1)
            {
                audioPanels.Clear();
                AudioSource[] audioSources = FindObjectsOfType(typeof(AudioSource)) as AudioSource[];
                foreach (AudioSource s in audioSources)
                    if(s.gameObject.GetComponent<Part>() != null)
                        audioPanels.Add(new ASEFilterPanel(s.gameObject, s));
                //add relevant filters
                foreach(ASEFilterPanel aPanel in audioPanels)
                    aPanel.AddKnobs(Knobs.lowpass | Knobs.distortion | Knobs.reverb);
                lastVesselPartCount = FlightGlobals.ActiveVessel.parts.Count;
            }
        }
    }//end class
}//namespace
