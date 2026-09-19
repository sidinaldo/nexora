using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Api.Seguranca;

/// <summary>A configuração de JSON da API, num lugar que o TESTE também alcança.
///
/// ⚠️ ESTAVA INLINE NO `Program.cs`, e por isso nenhum teste enxergava o formato que de fato sai
/// pelo fio. Foi assim que a prévia de importação passou a marcar toda linha como recusada sem
/// um teste vermelho: o enum saía `"Nova"`, a tela esperava `'nova'`, e os testes do painel
/// mockavam o que a tela esperava. Com a configuração aqui, um teste de serialização usa a MESMA
/// que o `AddJsonOptions` usa — não uma cópia que pode divergir dela.</summary>
public static class OpcoesJson
{
    public static void Configurar(JsonSerializerOptions o)
    {
        // Enum como TEXTO, nao como numero. Sem isto o painel recebe `papel: 2` e teria que
        // manter a ordem do enum C# duplicada no TypeScript — e qualquer valor novo inserido
        // no meio do enum quebraria o front em silencio.
        //
        // ⚠️ SEM POLITICA DE NOME: sai `"Nova"`, como no C#. Quem precisa de minuscula usa
        // `EnumMinusculo` na propriedade — ver o porque la.
        o.Converters.Add(new JsonStringEnumConverter());

        // `<input type="time">` manda "14:30", sem segundos — e o conversor padrao de TimeOnly
        // exige "14:30:00" e devolve 400. Aceitar as duas formas AQUI, e nao no cliente: o
        // servidor e quem define o contrato, e todo navegador manda o formato curto.
        o.Converters.Add(new ConversorHoraFlexivel());
        o.Converters.Add(new ConversorHoraFlexivelNulavel());
    }
}
