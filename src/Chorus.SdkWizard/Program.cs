using Chorus.SdkWizard;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: chorus-sdk-wizard <unity-project-root>");
    Console.Error.WriteLine("The wizard audits one Unity project for the Chorus SDK: package");
    Console.Error.WriteLine("dependencies, the public configuration surface, the layer-3 secret");
    Console.Error.WriteLine("scan and the platform materials (FIX-U3D-04).");
    return 2;
}

return SdkWizardRunner.Run(Console.Out, args[0]);
