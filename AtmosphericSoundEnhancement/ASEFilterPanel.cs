using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEngine;

namespace ASE
{
    /// <summary>
    /// Control knobs for adjusting filters.
    /// </summary>
    [FlagsAttribute]
    public enum Knobs
    {
        none = 0x0,
        lowpass = 0x1,
        distortion = 0x2,
        distortionlowpass = distortion | lowpass,
        reverb = 0x4,
        reverblowpass = reverb | lowpass,
        reverbdistortion = reverb | distortion,
        volume = 0x8,
        filters = reverb | distortion | lowpass,
        all = volume | reverb | distortion | lowpass
    }

    /// <summary>
    /// ASEFilterPanel to cache and manipulate filter components on AudioSource.
    /// NOT suitable for live spacesynth jam sessions.
    /// </summary>
    public class ASEFilterPanel
    {
        public GameObject gameObj;
        public AudioSource input;
        public AudioReverbFilter reverb;
        public AudioDistortionFilter distortion;
        public AudioLowPassFilter lowpass;

        public ASEFilterPanel(GameObject gObj, AudioSource aSource)
        {
            gameObj = gObj;
            input = aSource;
        }

        /// <summary>
        /// Add effects knobs to panel.  Attempts to recycle existing filters.
        /// </summary>
        /// <param name="select">Select multiple knobs via logical OR.
        /// e.g. Knobs.distortion | Knobs.reverb</param>
        /// <remarks>TODO Figure out a way to make filters chain (lowpass LAST).</remarks>
        public void AddKnobs(Knobs select)
        {
            if ((select & Knobs.distortion) == Knobs.distortion)
            {
                distortion = gameObj.GetComponent<AudioDistortionFilter>();
                if (distortion == null)
                {
                    //Debug.Log("ASE -- Adding distortion filter");
                    distortion = gameObj.AddComponent<AudioDistortionFilter>();
                }
            }
            if ((select & Knobs.reverb) == Knobs.reverb)
            {
                reverb = gameObj.GetComponent<AudioReverbFilter>();
                if (reverb == null)
                {
                    //Debug.Log("ASE -- Adding reverb filter");
                    reverb = gameObj.AddComponent<AudioReverbFilter>();
                }
            }
            if ((select & Knobs.lowpass) == Knobs.lowpass)
            {
                lowpass = gameObj.GetComponent<AudioLowPassFilter>();
                if (lowpass == null)
                {
                    //Debug.Log("ASE -- Adding low-pass filter");
                    lowpass = gameObj.AddComponent<AudioLowPassFilter>();
                }
                lowpass.lowpassResonaceQ = 0f;
            }
        }

        /// <summary>
        /// Adjust effects knobs
        /// </summary>
        /// <param name="select">Select multiple knobs via logical OR.
        /// e.g. Knobs.distortion | Knobs.reverb</param>
        /// <param name="setting">-1 to turn off</param>
        /// <remarks>TODO error checking on filter knobs.</remarks>
        public void SetKnobs(Knobs select, float setting)
        {
            if ((select & Knobs.lowpass) == Knobs.lowpass)
            {
                if (setting >= 0)
                {
                    //Debug.Log("ASE -- Setting low-pass filter to " + setting);
                    lowpass.enabled = true;
                    lowpass.cutoffFrequency = setting;
                }
                else
                {
                    //Debug.Log("ASE -- Disabling-low pass filter");
                    lowpass.enabled = false;
                }
            }
            if ((select & Knobs.distortion) == Knobs.distortion)
            {
                if (setting >= 0)
                {
                    //Debug.Log("ASE -- Setting distortion filter to " + setting);
                    distortion.enabled = true;
                    distortion.distortionLevel = setting;
                }
                else
                {
                    //Debug.Log("ASE -- Disabling distortion filter");
                    distortion.enabled = false;
                }
            }
            if ((select & Knobs.reverb) == Knobs.reverb)
            {
                if (setting >= 0)
                {
                    //Debug.Log("ASE -- Setting reverb filter to " + setting);
                    reverb.enabled = true;
                    reverb.reverbLevel = setting;
                }
                else
                {
                    //Debug.Log("ASE -- Disabling reverb filter");
                    reverb.enabled = false;
                }
            }
            if ((select & Knobs.volume) == Knobs.volume)
                if (setting >= 0)
                {
                    //Debug.Log("ASE -- Setting volume to " + setting);
                    input.volume = setting;
                }
                else
                {
                    //Debug.Log("ASE -- Muting volume");
                    input.volume = 0;
                }
        }
    }//endclass
}
