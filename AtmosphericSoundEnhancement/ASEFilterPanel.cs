using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEngine;

namespace ASE
{
    /* Control knobs for adjusting filters.
     */
    [FlagsAttribute]
    public enum Knobs
    { none=0x0
    , lowpass=0x1
    , distortion=0x2
    , reverb=0x4
    , volume=0x8
    , shockwave = distortion | reverb
    , filters = lowpass | distortion | reverb
    , all = lowpass | distortion | reverb | volume
    }

    /* ASEFilterPanel to cache and manipulate filter components on AudioSource.
     * NOT suitable for live spacesynth jam sessions.
     */
    public class ASEFilterPanel
    {
        public GameObject gameObj;
        public AudioSource input;
        public AudioReverbFilter reverb;
        public AudioDistortionFilter distortion;
        public AudioLowPassFilter lowpass;

        /* @param Part aPart Provide reference.
         * @param AudioSource aSource audio input.
         */
        public ASEFilterPanel(GameObject gObj, AudioSource aSource)
        {
            gameObj = gObj;
            input = aSource;
        }

        /* Add effects knobs to panel.  Attempts to recycle existing filters.
         *
         * @param Knobs Select multiple knobs via logical OR.
         *   e.g. Knobs.distortion | Knobs.reverb
         *
         * @notes TODO Figure out a way to make filters chain (lowpass LAST)
         */
        public void AddKnobs(Knobs select)
        {
            if((select & Knobs.distortion) == Knobs.distortion)
            {
                distortion = gameObj.GetComponent<AudioDistortionFilter>();
                if(distortion == null)
                    distortion = gameObj.AddComponent<AudioDistortionFilter>();
            }
            if((select & Knobs.reverb) == Knobs.reverb)
            {
                reverb = gameObj.GetComponent<AudioReverbFilter>();
                if(reverb == null)
                    reverb = gameObj.AddComponent<AudioReverbFilter>();
            }
            if((select & Knobs.lowpass) == Knobs.lowpass)
            {
                lowpass = gameObj.GetComponent<AudioLowPassFilter>();
                if(lowpass == null)
                    lowpass = gameObj.AddComponent<AudioLowPassFilter>();
                lowpass.lowpassResonaceQ = 0f;
            }
        }


        /* Adjust effects knobs
         *
         * @param Knobs select multiple knobs via logical OR.
         *   e.g. Knobs.distortion | Knobs.reverb
         * @param float setting -1 to turn off.
         *
         * @notes TODO error checking on filter knobs
         */ 
        public void SetKnobs(Knobs select, float setting)
        {
            if((select & Knobs.lowpass) == Knobs.lowpass)
            {
                if(setting >= 0)
                {
                    lowpass.enabled = true;
                    lowpass.cutoffFrequency = setting;
                }
                else
                    lowpass.enabled = false;
            }
            if((select & Knobs.distortion) == Knobs.distortion)
            {
                if(setting >= 0)
                {
                    distortion.enabled = true;
                    distortion.distortionLevel = setting;
                }
                else
                    distortion.enabled = false;
            }
            if((select & Knobs.reverb) == Knobs.reverb)
            {
                if(setting >= 0)
                {
                    reverb.enabled = true;
                    reverb.reverbLevel = setting;
                }
                else
                    reverb.enabled = false;
            }
            if((select & Knobs.volume) == Knobs.volume)
                if(setting >= 0)
                    input.volume = setting;
                else
                    input.volume = 0;
        }
    }//endclass
}
