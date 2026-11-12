using System.Buffers.Binary;
using AudioConsole.Core;
using AudioConsole.Windows;

internal static class RedirectHardwareTest
{
    public static async Task Run(string output, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var redirect = new WasapiAudioRedirect();
        var destinations = redirect.GetDevices();
        check(destinations.Count > 0, "At least one render endpoint is required.");
        if (destinations.Count == 0) return;
        // Choose the normal default render endpoint, then inspect exactly that endpoint by loopback.
        using var input = new WasapiAudioInput(StereoMixer.Format);
        var defaultInput = input.GetDevices(AudioSourceKind.ComputerOutput)[0];
        var destination = destinations.First(d => "loopback:" + d.Id == defaultInput.Identity);
        using var capture = new AudioConsoleService(input, temporaryRoot: output);
        capture.SelectInput("loopback:" + destination.Id);
        await Task.Delay(200);
        capture.StartRecording();
        redirect.StartLive(destination.Id, StereoMixer.Format);
        var samples = new float[960];
        var pcm = new byte[48000 * 2 * 2 / 2];
        for (var i = 0; i < pcm.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 330 * (i / 2) / 48000) * 327));
        for (var block = 0; block < 40; block++)
        {
            for (var i = 0; i < samples.Length; i++) samples[i] = (float)(Math.Sin(2 * Math.PI * 330 * (block * 480 + i / 2) / 48000) * 0.01);
            redirect.WriteSamples(samples);
            await Task.Delay(10);
        }
        check(redirect.IsActive && redirect.Error == null, "Native live output remains active.");
        await Task.Delay(150);
        redirect.Stop();
        capture.StopRecording();
        var live = File.ReadAllBytes(capture.SaveWav(Path.Combine(output, "redirect-live-hardware.wav")));
        check(HasSignal(live), "Chosen native endpoint receives live redirected audio.");
        var file = Path.Combine(output, "redirect-file-source.wav");
        using (var stream = File.Create(file)) WavWriter.Write(stream, StereoMixer.Format, pcm);
        capture.StartRecording();
        redirect.PlayFile(destination.Id, file);
        check(redirect.IsActive, "Native file redirect starts on selected endpoint.");
        await Task.Delay(200);
        check(redirect.Position > TimeSpan.Zero, "Redirected file clock advances.");
        await Task.Delay(600);
        check(!redirect.IsActive && redirect.Error == null, "Native file redirect completes naturally.");
        capture.StopRecording();
        var recordedFile = File.ReadAllBytes(capture.SaveWav(Path.Combine(output, "redirect-file-hardware.wav")));
        check(HasSignal(recordedFile), "Chosen native endpoint receives file audio.");
        Console.WriteLine("Native redirect endpoint: " + destination.Name);
    }

    private static bool HasSignal(byte[] wav)
    {
        for (var i = 44; i + 1 < wav.Length; i += 2)
            if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))) > 50) return true;
        return false;
    }
}
