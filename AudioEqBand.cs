namespace EqualTune.AudioLab;

internal readonly record struct AudioEqBand(double Frequency, double GainDb, double Q, string Filter = FilterNames.Bell);
