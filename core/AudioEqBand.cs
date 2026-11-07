namespace EqualTune.AudioLab;

public readonly record struct AudioEqBand(double Frequency, double GainDb, double Q, string Filter = FilterNames.Bell);
