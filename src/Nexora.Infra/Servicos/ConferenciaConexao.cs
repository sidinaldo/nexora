using Nexora.Core.Entidades;
using Nexora.Core.Whatsapp;

namespace Nexora.Infra.Servicos;

/// <summary>===================== O ESTADO DO NÚMERO, CONFERIDO NA EVOLUTION =====================
///
/// Quem conta ao banco que um número caiu é o webhook `connection.update`. ⚠️ SÓ ELE CONTAVA, e o
/// aviso se perde: com a API desligada, reiniciando ou num deploy mais longo que as ~20 minutos de
/// reentrega da Evolution, a queda nunca é gravada. Aconteceu em desenvolvimento: o número caiu em
/// 01/10 (código 401, sessão encerrada no celular) e a tela, o banner e o "Conectado desde" seguiram
/// dizendo que estava tudo bem por seis dias.
///
/// Esta é a conferência: pergunta à Evolution e corrige a linha. Três caminhos a usam, e por isso
/// ela mora num lugar só — três cópias do `switch` abaixo divergiriam:
///   · o polling do pareamento (`ServicoConexoes.StatusAsync`), com o QR na tela;
///   · a abertura da tela de números (`ServicoConexoes.ConferirAsync`);
///   · o verificador periódico (`VerificadorConexoes`), que vale para o banner de quem nem abre a
///     tela.
///
/// NÃO SALVA: quem chama decide quando — o verificador salva uma conexão por vez, para a falha de
/// uma não desfazer a correção das outras.
/// ==========================================================================================</summary>
public static class ConferenciaConexao
{
    /// <summary>O estado cru da Evolution e se a linha mudou. Só mexe na linha quando o estado
    /// MUDA — o pareamento chama isto a cada 3s, e sem o guarda seria uma escrita por tick.</summary>
    public static async Task<(string Estado, bool Mudou)> ConferirAsync(
        Conexao conexao, IClienteWhatsApp cliente, DateTime agora, CancellationToken ct)
    {
        var estado = await cliente.StatusInstanciaAsync(conexao.InstanceName, ct);
        var conectado = estado == "open";

        // 'offline' e distinto de 'desconectado': offline = a Evolution nao respondeu (problema
        // nosso, o cliente nao tem o que fazer); desconectado = o numero caiu e ele precisa
        // reparear. Colapsar os dois manda a pessoa escanear QR a toa.
        var novo = estado switch
        {
            "open" => StatusConexao.Conectado,
            "connecting" => StatusConexao.Conectando,
            "nao_criada" => StatusConexao.NaoCriada,
            "offline" => StatusConexao.Offline,
            _ => StatusConexao.Desconectado
        };

        var mudou = false;

        if (conexao.Status != novo)
        {
            conexao.Status = novo;
            conexao.StatusEm = agora;
            if (conectado && conexao.ConectadoEm is null) conexao.ConectadoEm = agora;
            if (!conectado && novo == StatusConexao.Desconectado) conexao.DesconectadoEm = agora;
            mudou = true;
        }

        // Rede de seguranca: conectada mas sem numero (webhook connection.update perdido) ->
        // backfill do numero e do perfil.
        if (conectado && string.IsNullOrEmpty(conexao.Numero))
        {
            var det = await cliente.ObterDetalhesInstanciaAsync(conexao.InstanceName, ct);
            if (det?.OwnerJid is { Length: > 0 } jid)
            {
                conexao.Numero = CanonicalizadorTelefone.Canonicalizar(jid.Split('@')[0]);
                conexao.PerfilNome ??= det.PerfilNome;
                conexao.PerfilFotoUrl ??= det.PerfilFotoUrl;
                mudou = true;
            }
        }

        return (estado, mudou);
    }
}
