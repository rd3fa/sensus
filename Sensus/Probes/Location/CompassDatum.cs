﻿using System;

namespace Sensus.Probes.Location
{
    public class CompassDatum : Datum
    {
        private double _heading;

        public double Heading
        {
            get { return _heading; }
        }

        public CompassDatum(int probeId, DateTimeOffset timestamp, double heading)
            : base(probeId, timestamp)
        {
            _heading = heading;
        }

        public override string ToString()
        {
            return base.ToString() + Environment.NewLine +
                   "Heading:  " + _heading;
        }
    }
}