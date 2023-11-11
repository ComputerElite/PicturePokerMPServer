using System.Text.Json;
using ComputerUtils.Logging;

namespace PicturePokerMPServer;

public class Config
{
    public string publicAddress { get; set; } = "";
    public int port { get; set; } = 20006;

    public static Config LoadConfig()
    {
        string configLocation = Env.workingDir + "data" + Path.DirectorySeparatorChar + "config.json";
        if (!File.Exists(configLocation)) File.WriteAllText(configLocation, JsonSerializer.Serialize(new Config()));
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(configLocation));
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Env.workingDir + "data" + Path.DirectorySeparatorChar + "config.json", JsonSerializer.Serialize(this));
        }
        catch (Exception e)
        {
            Logger.Log("couldn't save config: " + e.ToString(), LoggingType.Warning);
        }
    }
}