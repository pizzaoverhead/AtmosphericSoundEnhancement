using System;
using System.Collections.Generic;

using UnityEngine;
//using ferram4;

namespace ASE
{
    public interface IAtmoDataProvider
    {
        float GetMach();
        double GetDensity();
    }

    //public class SharedFARAtmoDataProvider : IAtmoDataProvider
    //{
    //    public float GetMach()
    //    {
    //        return FARControlSys.ActiveControlSys.MachNumber;
    //    }

    //    public double GetDensity()
    //    {
    //        return FARControlSys.ActiveControlSys.vessel.atmDensity;
    //    }
    //}

    public class FallbackAtmoDataProvider : IAtmoDataProvider
    {
        public float GetMach()
        {
            float temperature = FlightGlobals.ActiveVessel.flightIntegrator.getExternalTemperature();
            float sos = Mathf.Sqrt(1.4f * (temperature + 273.15f) * 287f);//Ideal gas
            float speed = FlightGlobals.ActiveVessel.GetSrfVelocity().magnitude;
            return speed / sos;
        }

        public double GetDensity()
        {
            return FlightGlobals.ActiveVessel.atmDensity;
        }
    }

    public class AtmoDataProvider
    {
        private static IAtmoDataProvider _adp;

        public static IAtmoDataProvider Get()
        {
            if (_adp != null) return _adp;
            //if (testFAR())
            //    _adp = new SharedFARAtmoDataProvider();
            //else
                _adp = new FallbackAtmoDataProvider();
            return _adp;
        }

        //private static bool testFAR()
        //{
        //    try
        //    {
        //        var foo = FARControlSys.activeMach;
        //        Debug.Log("ASE: FAR is available.");
        //        return true;
        //    }
        //    catch (Exception) { }
        //    Debug.Log("ASE: FAR is missing. Using fallback implementation.");
        //    return false;
        //}
    }
}
