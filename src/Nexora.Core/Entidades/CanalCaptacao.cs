namespace Nexora.Core.Entidades;

/// <summary>Um canal de captação por QR Code ou link: "Balcão da loja", "Panfleto Julho",
/// "Link na bio".
///
/// ===================== COMO O RASTREIO FUNCIONA (e por que é frágil de propósito) =====================
/// O canal gera um link `wa.me` com um CÓDIGO CURTO no texto pré-preenchido. Quem escaneia abre o
/// WhatsApp com a frase pronta e envia; o webhook lê o código na primeira mensagem e carimba a
/// origem do contato.
///
/// Não há nenhuma outra amarração: nem redirecionador nosso, nem parâmetro que o WhatsApp
/// preserve. Isso significa que **a pessoa pode apagar o código antes de mandar**, e vai
/// acontecer. Quando acontece, o lead entra como `whatsapp` e ninguém fica sabendo de onde ele
/// veio — que é melhor que atribuir ao canal errado.
///
/// Consequência que não dá para esconder: **este contador é piso, não total.** Não existe
/// denominador. Um scan que perdeu o código é indistinguível de alguém que nunca escaneou, porque
/// quem hospeda o `wa.me` é a Meta e ela não nos conta nada. Medir "taxa de preservação" exigiria
/// um redirecionador nosso (`nexora.app/q/{codigo}` → 302), que não existe.
/// =====================================================================================================</summary>
public class CanalCaptacao : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>Nome interno, que também vai para `contatos.origem_detalhe` — é o que o vendedor
    /// lê no card para saber de onde o lead veio.</summary>
    public string Nome { get; set; } = null!;

    /// <summary>O código que viaja no texto do WhatsApp, SEM o `#` (ex.: `a3f9`).
    ///
    /// Único por EMPRESA, não globalmente: quem resolve o tenant é o `instance_name` da conexão
    /// que recebeu a mensagem, e a busca do código já sai recortada por empresa. Único global
    /// gastaria espaço de código à toa e faria dois clientes disputarem `a3f9`.
    ///
    /// Guardado em minúsculas. Quem digita à mão pode variar a caixa, e a comparação normaliza o
    /// PARÂMETRO — nunca a coluna, que precisa continuar indexável.</summary>
    public string Codigo { get; set; } = null!;

    /// <summary>Por qual NÚMERO este canal atende. O link `wa.me` embute o telefone, então o canal
    /// não é da empresa — é de um número dela.
    ///
    /// ⚠️ O número entra no material IMPRESSO. Trocar o chip desta conexão invalida todo panfleto
    /// e cartão já distribuídos, e não há nada que o sistema possa fazer a respeito: o QR já está
    /// no papel. A tela avisa; o aviso de troca de chip do ARQ-2 (`numero_anterior`) é o outro
    /// lado do mesmo alerta.</summary>
    public long ConexaoId { get; set; }

    /// <summary>A origem que o lead recebe. É `qrcode` para balcão e panfleto, mas o mesmo
    /// mecanismo serve para `instagram` (link na bio) e `indicacao` (parceiro) — e é por isso que
    /// não fica travado em `qrcode`.</summary>
    public OrigemLead Origem { get; set; } = OrigemLead.Qrcode;

    /// <summary>Desligado NÃO apaga: o material impresso continua no mundo, e o histórico dos
    /// leads que já vieram continua fazendo sentido. Canal desligado para de ATRIBUIR — o lead
    /// ainda entra, como `whatsapp`.</summary>
    public bool Ativo { get; set; } = true;

    /// <summary>Quantos contatos foram CRIADOS com este código. Só a criação conta: contato que
    /// já existia mantém a origem da primeira vez, e somá-lo aqui contaria a mesma pessoa duas
    /// vezes.
    ///
    /// Denormalizado pelo mesmo motivo de `formularios_captura.leads_recebidos`: contar por
    /// `origem_detalhe` daria número errado no dia em que alguém renomeasse o canal.</summary>
    public int LeadsRecebidos { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Conexao Conexao { get; set; } = null!;
}
