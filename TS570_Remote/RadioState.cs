using System;

namespace TS570_Remote
{
    internal enum RadioMode
    {
        Lsb,
        Usb,
        Cw,
        Fsk,
        Fm,
        Am
    }

    internal sealed class RadioState
    {
        public int VfoAHz { get; set; } = 7_074_000;
        public int VfoBHz { get; set; } = 7_074_000;
        public int ActiveVfo { get; set; } = 0; // 0=A, 1=B

        public RadioMode Mode { get; set; } = RadioMode.Usb;
        public bool IsTx { get; set; }
        public bool SplitOn { get; set; }
        public bool TfSetOn { get; set; }
        public bool RitOn { get; set; }
        public bool XitOn { get; set; }
        public int RitXitValue { get; set; } = 50;
        public int RitOffsetCentiKhz { get; set; } // -999..999 => -9.99..9.99

        public bool AttOn { get; set; }
        public bool PreAmpOn { get; set; }
        public bool VoxOn { get; set; }
        public bool ProcOn { get; set; }
        public bool AtTuneOn { get; set; }
        public bool NbOn { get; set; }
        public bool AgcFast { get; set; }
        public bool FineOn { get; set; }
        public bool FLockOn { get; set; }
        public bool Step1MHz { get; set; }
        public bool BcOn { get; set; }
        public bool ScanOn { get; set; }
        public bool ScanHoldOnSignal { get; set; } = true;
        public int ScanDirection { get; set; } = 1; // 1=up, -1=down
        public bool MemMode { get; set; }

        public int NrState { get; set; } // 0,1,2
        public int FilterIndex { get; set; }
        public bool FmWideDeviation { get; set; } = true;
        public bool SsbAmWideFilter { get; set; } = true;
        public int CwFilterIndex { get; set; } = 8;  // default around 600Hz
        public int FskFilterIndex { get; set; } = 3; // default 1.5kHz
        public int AntSel { get; set; } = 1; // 1,2
        public int QuickMemChannel { get; set; } = 1; // 1..5
        public int SelectedMemoryChannel { get; set; } // 0..99

        public int AfValue { get; set; } = 50;
        public int RfValue { get; set; } = 50;
        public int IfShiftValue { get; set; } = 50;
        public int SqlValue { get; set; } = 50;
        public int DspHighValue { get; set; } = 50;
        public int DspLowValue { get; set; } = 50;
        public int PhonesValue { get; set; } = 50;
        public int MicGainValue { get; set; } = 50;

        public int TxMicValue { get; set; } = 50;
        public int TxPwrValue { get; set; } = 100;
        public int TxKeyValue { get; set; } = 50;
        public int TxDelayValue { get; set; } = 50;
        public int MultiChValue { get; set; } = 1;

        public DateTime ManualFreqOverrideUntilUtc { get; set; } = DateTime.MinValue;

        public int CurrentVfoHz
        {
            get => ActiveVfo == 0 ? VfoAHz : VfoBHz;
            set
            {
                if (ActiveVfo == 0) VfoAHz = value;
                else VfoBHz = value;
            }
        }
    }
}
