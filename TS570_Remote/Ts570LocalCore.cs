using System;
using System.Collections.Generic;

namespace TS570_Remote
{
    internal sealed class Ts570LocalCore
    {
        private sealed class MemoryChannel
        {
            public bool Used { get; set; }
            public int FrequencyHz { get; set; }
            public RadioMode Mode { get; set; }
        }

        private static readonly int[] HamBandCentersHz =
        {
            1_810_000, 3_500_000, 7_000_000, 10_100_000, 14_000_000,
            18_068_000, 21_000_000, 24_890_000, 28_000_000, 50_000_000
        };

        private readonly int[] _quickMemory = new int[5];
        private readonly MemoryChannel[] _memoryChannels = new MemoryChannel[100];

        public RadioState State { get; }

        public Ts570LocalCore(RadioState state)
        {
            State = state;
            for (int i = 0; i < _quickMemory.Length; i++)
                _quickMemory[i] = state.CurrentVfoHz;
            for (int i = 0; i < _memoryChannels.Length; i++)
            {
                _memoryChannels[i] = new MemoryChannel
                {
                    Used = false,
                    FrequencyHz = state.CurrentVfoHz,
                    Mode = state.Mode
                };
            }
        }

        public int Clamp01To100(int value) => Math.Clamp(value, 0, 100);

        public void ToggleAtt() => State.AttOn = !State.AttOn;
        public void TogglePreAmp() => State.PreAmpOn = !State.PreAmpOn;
        public void ToggleVox() => State.VoxOn = !State.VoxOn;
        public void ToggleProc() => State.ProcOn = !State.ProcOn;
        public void ToggleSend() => State.IsTx = !State.IsTx;
        public void ToggleAtTune() => State.AtTuneOn = !State.AtTuneOn;
        public int CycleNr() => State.NrState = (State.NrState + 1) % 3;
        public void ToggleBc() => State.BcOn = !State.BcOn;
        public void ToggleSplit() => State.SplitOn = !State.SplitOn;
        public void ToggleRit() => State.RitOn = !State.RitOn;
        public void ToggleXit() => State.XitOn = !State.XitOn;
        public void ToggleMemMode() => State.MemMode = !State.MemMode;
        public void ToggleScan() => State.ScanOn = !State.ScanOn;
        public void SetScanDirection(int direction) => State.ScanDirection = direction >= 0 ? 1 : -1;
        public void ToggleFine() => State.FineOn = !State.FineOn;
        public void ToggleNb() => State.NbOn = !State.NbOn;
        public void ToggleAgcFast() => State.AgcFast = !State.AgcFast;
        public void ToggleFLock() => State.FLockOn = !State.FLockOn;
        public void Toggle1MHz() => State.Step1MHz = !State.Step1MHz;
        public void ToggleAnt() => State.AntSel = State.AntSel == 1 ? 2 : 1;

        public void ToggleAB() => State.ActiveVfo = 1 - State.ActiveVfo;

        public void CopyAtoB() => State.VfoBHz = State.VfoAHz;

        public void ClearRitXit()
        {
            State.RitXitValue = 50;
            State.RitOffsetCentiKhz = 0;
            State.RitOn = false;
            State.XitOn = false;
        }

        public int AdjustRitOffset(int deltaCentiKhz)
        {
            State.RitOffsetCentiKhz = Math.Clamp(State.RitOffsetCentiKhz + deltaCentiKhz, -999, 999);
            return State.RitOffsetCentiKhz;
        }

        public int GetRitOffsetHz()
            => State.RitOffsetCentiKhz * 10;

        public int ChangeBand(int direction)
        {
            int currentHz = State.CurrentVfoHz;
            int currentIndex = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < HamBandCentersHz.Length; i++)
            {
                int d = Math.Abs(HamBandCentersHz[i] - currentHz);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    currentIndex = i;
                }
            }

            int nextIndex = Math.Clamp(currentIndex + direction, 0, HamBandCentersHz.Length - 1);
            State.CurrentVfoHz = HamBandCentersHz[nextIndex];
            return State.CurrentVfoHz;
        }

        public int NudgeFrequency(int deltaHz)
        {
            State.CurrentVfoHz = Math.Max(30_000, State.CurrentVfoHz + deltaHz);
            State.ManualFreqOverrideUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            return State.CurrentVfoHz;
        }

        public int NudgeVfoFrequency(int vfoIndex, int deltaHz)
        {
            int current = vfoIndex == 0 ? State.VfoAHz : State.VfoBHz;
            int updated = Math.Max(30_000, current + deltaHz);
            if (vfoIndex == 0) State.VfoAHz = updated;
            else State.VfoBHz = updated;
            State.ManualFreqOverrideUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            return updated;
        }

        public int SetCurrentFrequency(int hz)
        {
            State.CurrentVfoHz = Math.Max(30_000, hz);
            State.ManualFreqOverrideUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            return State.CurrentVfoHz;
        }

        public int SetVfoFrequency(int vfoIndex, int hz)
        {
            int clamped = Math.Max(30_000, hz);
            if (vfoIndex == 0) State.VfoAHz = clamped;
            else State.VfoBHz = clamped;
            State.ManualFreqOverrideUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            return clamped;
        }

        public void CycleLsbUsb()
            => State.Mode = State.Mode == RadioMode.Usb ? RadioMode.Lsb : RadioMode.Usb;

        public void CycleCwFsk()
            => State.Mode = State.Mode == RadioMode.Cw ? RadioMode.Fsk : RadioMode.Cw;

        public void CycleFmAm()
            => State.Mode = State.Mode == RadioMode.Fm ? RadioMode.Am : RadioMode.Fm;

        public int CycleFilter()
        {
            State.FilterIndex = (State.FilterIndex + 1) % 5;
            return State.FilterIndex;
        }

        public void AdjustFilter(int direction)
        {
            switch (State.Mode)
            {
                case RadioMode.Fm:
                    State.FmWideDeviation = direction >= 0;
                    break;
                case RadioMode.Cw:
                    State.CwFilterIndex = Math.Clamp(State.CwFilterIndex + direction, 0, CwFilterTexts.Length - 1);
                    break;
                case RadioMode.Fsk:
                    State.FskFilterIndex = Math.Clamp(State.FskFilterIndex + direction, 0, FskFilterTexts.Length - 1);
                    break;
                default:
                    State.SsbAmWideFilter = direction >= 0;
                    break;
            }
        }

        public string GetCurrentFilterText()
        {
            return State.Mode switch
            {
                RadioMode.Fm => State.FmWideDeviation ? "FM-WID" : "FM-NAR",
                RadioMode.Cw => CwFilterTexts[Math.Clamp(State.CwFilterIndex, 0, CwFilterTexts.Length - 1)],
                RadioMode.Fsk => FskFilterTexts[Math.Clamp(State.FskFilterIndex, 0, FskFilterTexts.Length - 1)],
                _ => State.SsbAmWideFilter ? "FIL-WID" : "FIL-NAR"
            };
        }

        public int NextQuickMemChannel()
        {
            State.QuickMemChannel = State.QuickMemChannel % 5 + 1;
            return State.QuickMemChannel;
        }

        public int PrevQuickMemChannel()
        {
            State.QuickMemChannel = State.QuickMemChannel > 1 ? State.QuickMemChannel - 1 : 5;
            return State.QuickMemChannel;
        }

        public void StoreQuickMemory()
        {
            _quickMemory[State.QuickMemChannel - 1] = State.CurrentVfoHz;
        }

        public int RecallQuickMemory()
        {
            int freq = _quickMemory[State.QuickMemChannel - 1];
            return SetCurrentFrequency(freq);
        }

        public void StoreMemoryChannel(int channel)
        {
            int idx = Math.Clamp(channel, 0, _memoryChannels.Length - 1);
            _memoryChannels[idx].Used = true;
            _memoryChannels[idx].FrequencyHz = State.CurrentVfoHz;
            _memoryChannels[idx].Mode = State.Mode;
            State.SelectedMemoryChannel = idx;
        }

        public bool RecallMemoryChannel(int channel)
        {
            int idx = Math.Clamp(channel, 0, _memoryChannels.Length - 1);
            if (!_memoryChannels[idx].Used)
                return false;

            State.SelectedMemoryChannel = idx;
            SetCurrentFrequency(_memoryChannels[idx].FrequencyHz);
            State.Mode = _memoryChannels[idx].Mode;
            return true;
        }

        public int StepMemoryChannel(int direction)
        {
            int idx = State.SelectedMemoryChannel;
            idx = (idx + direction + _memoryChannels.Length) % _memoryChannels.Length;
            State.SelectedMemoryChannel = idx;
            return idx;
        }

        public bool IsMemoryChannelUsed(int channel)
            => _memoryChannels[Math.Clamp(channel, 0, _memoryChannels.Length - 1)].Used;

        public (int min, int max) GetTxPowerLimits()
            => State.Mode == RadioMode.Am ? (5, 25) : (5, 100);

        public int NormalizeTxPower(int watts)
        {
            var lim = GetTxPowerLimits();
            int snapped = (int)Math.Round(watts / 5.0) * 5;
            return Math.Clamp(snapped, lim.min, lim.max);
        }

        public static IReadOnlyList<int> FilterWidthsHz => new[] { 2400, 1800, 600, 300, 250 };

        private static readonly string[] CwFilterTexts = { "CW 050", "CW 080", "CW 100", "CW 150", "CW 200", "CW 300", "CW 400", "CW 500", "CW 600", "CW 1.0", "CW 2.0" };
        private static readonly string[] FskFilterTexts = { "FSK250", "FSK500", "FSK1.0", "FSK1.5" };
    }
}
