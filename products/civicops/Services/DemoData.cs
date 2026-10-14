using CivicOps.Models;
namespace CivicOps.Services;
public static class DemoData
{
    public static readonly IncidentRequest[] Cases =
    [
        new("Burst pipe leaking outside the synthetic library.", "Demo North", Source: "web", ResidentLanguage: "en"),
        new("Burst pipe leaking outside the synthetic library.", "Demo North", Source: "elevenlabs-chat", ConversationId: "sim-demo-chat-2"),
        new("A live wire has fallen near the synthetic park. Danger.", "Demo East", Source: "elevenlabs-voice", ConversationId: "sim-demo-call-3"),
        new("Stormwater drain blocked with leaves and water.", "Demo West", Source: "open311", ServiceCode: "ROADS"),
        new("Pothole outside the synthetic civic hall.", "Demo Central", Source: "web"),
        new("Rubbish dumping behind the synthetic depot.", "Demo South", Source: "elevenlabs-chat", ResidentLanguage: "af", ConversationId: "sim-demo-chat-6"),
        new("Die waterpyp lek langs die sintetiese saal.", "Demo West", Source: "open311", ResidentLanguage: "af", ServiceCode: "WATER"),
        new("Power outage at the synthetic service centre.", "Demo South", Source: "elevenlabs-voice", ConversationId: "sim-demo-call-8"),
        new("The synthetic community noticeboard needs inspection.", "Demo North", Source: "web"),
        new("Traffic light broken at the synthetic intersection.", "Demo East", Source: "open311", ServiceCode: "ROADS"),
        new("Waste collection missed at the synthetic hall.", "Demo Central", Source: "web"),
        new("Streetlight flickering by the synthetic depot.", "Demo West", Source: "elevenlabs-chat", ConversationId: "sim-demo-chat-12"),
        new("Water leak at the synthetic recreation field.", "Demo East", Source: "web"),
        new("Fire and trapped person reported at synthetic exercise site.", "Demo South", Source: "elevenlabs-voice", ConversationId: "sim-demo-call-14"),
        new("A strange humming noise needs inspection at the synthetic hall.", "Demo Central", Source: "web", ResidentLanguage: "af", ContactEmail: "resident@example.invalid", ConsentToContact: true, ContactPreference: "email")
    ];
}
