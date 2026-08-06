namespace Nexora.Infra.Servicos;

/// <summary>Um DIÁLOGO inteiro, escrito à mão.
///
/// `Turnos` alterna começando por ENTRADA: índice par é o cliente, ímpar é a empresa. É assim
/// porque no Nexora toda conversa nasce de uma mensagem que CHEGOU — o lead escreve primeiro, e
/// não existe caminho em que a empresa abre a conversa (ver ServicoCaptura: o formulário do site
/// cria contato e lembrete, e de propósito NÃO manda WhatsApp).
///
/// `TerminaEmEntrada` é redundante com o tamanho (par = termina em saída), e existe assim mesmo:
/// é por ele que o semeador escolhe o roteiro, e derivar isso na hora da escolha esconderia a
/// única propriedade que importa para casar com o estado da conversa.</summary>
public sealed record Roteiro(string Nome, string[] Turnos)
{
    public bool TerminaEmEntrada => Turnos.Length % 2 == 1;
}

/// <summary>OS DIÁLOGOS DE MENTIRA, e por que eles são escritos e não gerados.
///
/// ===================== O QUE HAVIA ANTES =====================
/// A semeadura escolhia a frase por `i % 8` de duas listas fixas. O resultado era coerente por
/// acidente e idêntico em toda conversa: a mesma pergunta na posição 3, a mesma resposta na 4, em
/// trezentas threads. Serve para ver que o balão desenha; não serve para ver a tela funcionando.
///
/// O que uma conversa de verdade tem, e uma alternância mecânica não:
///   • ASSUNTO — a resposta responde a pergunta anterior, e a seguinte se apoia nela;
///   • RITMO — mensagens curtas em rajada, depois um intervalo longo;
///   • DESFECHO — fechou, sumiu, remarcou, reclamou. É o que faz a caixa parecer uma caixa.
/// =============================================================
///
/// ===================== SEM VOCABULÁRIO DE COBRANÇA =====================
/// Nada de devedor, acordo, parcela, boleto ou régua. O Nexora é CRM de VENDA — o texto que
/// aparece na tela é a coisa mais fácil de contaminar com o domínio do Recupera, e a mais difícil
/// de perceber depois.
/// ======================================================================
///
/// Os roteiros são genéricos de propósito: orçamento, prazo, agendamento e entrega servem tanto
/// para a padaria quanto para a oficina, e não amarram a semeadura a um ramo.</summary>
public static class RoteirosConversa
{
    /// <summary>Terminam com o CLIENTE falando — ou seja, alguém está esperando resposta. São os
    /// que casam com conversa que tem `aguardando_desde` preenchido, e é neles que o semáforo
    /// acende.</summary>
    public static readonly Roteiro[] EsperandoResposta =
    [
        new("orçamento sem resposta",
        [
            "Boa tarde! Vi vocês no Instagram, ainda fazem orçamento sem compromisso?",
            "Boa tarde! Fazemos sim 😊 Me conta o que você precisa que eu já vejo aqui",
            "Preciso de um pra um espaço de uns 40m², é pra semana que vem",
            "Perfeito. Consegue me mandar uma foto do espaço? Ajuda a fechar o valor certinho",
            "Mandei no e-mail que tá no perfil de vocês",
            "Recebi! Vou montar aqui e te retorno ainda hoje com o número",
            "Beleza, fico no aguardo",
            "Oi! Consegui fechar em R$ 2.480, com material incluso. Posso te mandar o detalhado?",
            "Pode sim",
            "Mandei agora. Qualquer ajuste é só falar 👍",
            "Recebi. Ficou um pouco acima do que eu tinha em mente, tem margem?",
            "Consigo melhorar um pouco no pagamento à vista. Te mando duas opções?",
            "Manda por favor",
            "Mandei as duas. A segunda sai 8% menor à vista",
            "Vou conversar com meu sócio e te falo até amanhã",
            "Combinado! Fico à disposição 🙌",
            "Oi, bom dia! Conversei aqui e a gente quer seguir. Consegue começar dia 18?"
        ]),

        new("dúvida rápida",
        [
            "Oi! Vocês atendem no sábado?",
            "Oi! Atendemos das 8h às 13h 😊",
            "E precisa agendar ou pode chegar?",
            "Pode chegar, mas sábado costuma encher depois das 10h. Se quiser eu já reservo um horário pra você",
            "Reserva pras 9h então",
            "Reservado! 9h, sábado. Me confirma o nome completo?",
            "Marina Oliveira Prado",
            "Anotado, Marina 👍 Até sábado!",
            "Obrigada! Ah, esqueci: vocês aceitam cartão?"
        ]),

        new("primeiro contato pelo QR",
        [
            "Olá! Tenho interesse.",
            "Oi! Tudo bem? Vi que você chegou pelo nosso QR Code 😊 Como posso ajudar?",
            "Vi o cartaz na loja e queria entender como funciona",
            "Claro! Você faz o pedido por aqui mesmo e a gente entrega no endereço que você passar. Prazo médio de 2 dias úteis",
            "E tem valor mínimo?",
            "Tem sim, R$ 80. Abaixo disso a gente cobra a entrega separada",
            "Entendi. E se eu quiser retirar aí?",
            "Retirada é livre, sem mínimo. Só me avisa umas 2h antes pra deixar separado",
            "Perfeito. Vou montar minha lista e te mando ainda hoje",
            "Fico esperando! 🙌",
            "Oi, montei a lista. Consegue me passar o valor antes de eu confirmar?"
        ]),

        new("reclamação de prazo",
        [
            "Bom dia. Meu pedido era pra ontem e não chegou",
            "Bom dia! Poxa, me desculpa. Me passa o número do pedido que eu vejo agora mesmo",
            "É o 4417",
            "Achei aqui. Saiu ontem 16h mas voltou — o endereço tá sem o número do apartamento",
            "Ah não… é 302, bloco B",
            "Anotei! Já reprogramei pra hoje. Deve chegar até 17h",
            "Tudo bem, mas eu precisava ontem. Tem como priorizar?",
            "Vou pedir pro entregador subir na rota da manhã. Não garanto horário, mas fica antes do meio-dia",
            "Ok, obrigado",
            "Eu que agradeço a paciência 🙏 Assim que sair eu te aviso por aqui",
            "E chegou?"
        ]),

        new("indicação de amigo",
        [
            "Oi! A Camila me passou o contato de vocês",
            "Oi! Que bom, a Camila é nossa cliente há um tempão 😄 Em que posso ajudar?",
            "Ela falou muito bem do serviço. Queria saber os valores",
            "Depende do que você precisa. Me conta um pouco?",
            "É pra um evento pequeno, uns 30 convidados, dia 12",
            "Dá pra fazer sim! Pra 30 pessoas a gente costuma sugerir dois formatos. Te mando os dois com preço?",
            "Manda",
            "Enviei 👍 O primeiro sai R$ 1.150 e o segundo R$ 1.640, já com montagem",
            "Boa. O dia 12 ainda tá livre mesmo?",
            "Tá! Mas é o único de novembro que sobrou, então se puder me confirmar até quinta eu seguro pra você",
            "Confirmo até quinta então. Só uma coisa: tem opção sem lactose?"
        ]),

        new("volta depois de meses",
        [
            "Oi! Comprei com vocês ano passado, lembram?",
            "Oi! Deixa eu ver aqui… achei seu cadastro 😊 Tudo certo com o que você levou?",
            "Tudo, funcionou super bem. Por isso voltei",
            "Que ótimo saber! O que você precisa dessa vez?",
            "A mesma coisa, mas em dobro. Consegue?",
            "Consigo. O valor mudou um pouco desde o ano passado, tá bem?",
            "Quanto ficou?",
            "R$ 890 agora, era R$ 760. Como você já é cliente eu faço por 840",
            "Fechado. Pode faturar",
            "Perfeito! Vou precisar confirmar o endereço de entrega, é o mesmo?",
            "Mudei de endereço. Te mando agora"
        ])
    ];

    /// <summary>Terminam com a EMPRESA falando — ninguém está esperando resposta. São os que
    /// casam com conversa sem `aguardando_desde`: a bola está com o cliente, e é daqui que sai o
    /// follow-up automático quando ele some.</summary>
    public static readonly Roteiro[] BolaComOCliente =
    [
        new("fechou a venda",
        [
            "Oi, boa tarde! Vocês ainda têm aquele modelo que estava na vitrine?",
            "Boa tarde! Temos sim, o último 😄 Quer que eu separe?",
            "Quero! Quanto tá?",
            "R$ 1.290 à vista, ou 3x de R$ 450 no cartão",
            "Consigo pagar metade agora e metade na retirada?",
            "Consegue sim, sem problema",
            "Então fecha. Retiro amanhã de manhã",
            "Fechado! Separei aqui com seu nome. Te espero amanhã 🙌",
            "Combinado, obrigado!",
            "Eu que agradeço! Qualquer coisa é só chamar por aqui 😊"
        ]),

        new("pediu desconto e sumiu",
        [
            "Bom dia! Queria saber o preço, por favor",
            "Bom dia! Claro, já te passo. É pra qual tamanho?",
            "O médio mesmo",
            "O médio sai R$ 340",
            "Nossa, achei que fosse menos",
            "Entendo! Esse valor já inclui a instalação, que a maioria cobra separado. Sem ela fica R$ 265",
            "E qual o prazo?",
            "5 dias úteis pra entregar, instalação no mesmo dia se você preferir",
            "Vou pensar e te falo",
            "Sem problema! Deixo reservado até sexta, qualquer coisa é só chamar 👍"
        ]),

        new("remarcou o atendimento",
        [
            "Oi, tenho horário marcado pra hoje às 14h",
            "Oi! Tá aqui na agenda ✅",
            "Consigo mudar? Surgiu um imprevisto no trabalho",
            "Consegue sim. Prefere outro dia ou mais tarde hoje?",
            "Outro dia, se puder",
            "Tenho quinta às 10h ou sexta às 15h30",
            "Sexta fica melhor",
            "Marquei sexta 15h30 então. Liberei o horário de hoje 👍",
            "Obrigada pela compreensão!",
            "Imagina! Te mando um lembrete na quinta pra você não esquecer 😊"
        ]),

        new("orçamento entregue, esperando decisão",
        [
            "Oi! Consegue me mandar o orçamento hoje?",
            "Oi! Consigo. Só confirma pra mim: são 12 peças ou 15?",
            "15",
            "Perfeito, já monto e te mando",
            "Valeu",
            "Mandei! Ficou R$ 3.720 no total, com 10% de desconto se fechar até sexta",
            "Recebi. Vou levar pra aprovação interna",
            "Beleza! Qualquer dúvida na proposta é só chamar. Fico à disposição 🙌"
        ]),

        new("pós-venda resolvido",
        [
            "Boa tarde. O que comprei semana passada veio com um detalhe torto",
            "Boa tarde! Que chato isso 😕 Consegue me mandar uma foto?",
            "Mandei",
            "Vi aqui. Realmente veio com defeito — a gente troca sem custo. Prefere que eu envie outro ou você traz esse?",
            "Podem enviar outro",
            "Combinado. Sai amanhã e chega quinta. O entregador leva o novo e recolhe esse",
            "Perfeito, obrigado pela rapidez",
            "Nós que agradecemos a paciência 🙏 Já te aviso por aqui quando sair"
        ]),

        new("lead frio que só perguntou preço",
        [
            "quanto custa?",
            "Oi! Tudo bem? Depende do que você precisa — me dá uma ideia?",
            "só o basico mesmo",
            "O nosso plano mais simples sai R$ 190/mês. Quer que eu te explique o que entra?",
            "depois eu vejo",
            "Sem problema! Deixo meu contato salvo aqui, é só chamar quando quiser 👍"
        ])
    ];

    /// <summary>O roteiro que casa com o estado da conversa, escolhido de forma DETERMINÍSTICA
    /// pelo id — reexecutar a semeadura dá a mesma conversa na mesma thread.
    ///
    /// Casar com o estado é o ponto: a conversa já tem `aguardando_desde`, `ultima_mensagem_em` e
    /// a faixa do semáforo que a semeadura anterior distribuiu com cuidado. Escolher um roteiro
    /// que termine na direção errada quebraria o semáforo de todas elas de uma vez.</summary>
    public static Roteiro Escolher(bool terminaEmEntrada, long conversaId)
    {
        var lista = terminaEmEntrada ? EsperandoResposta : BolaComOCliente;
        return lista[(int)(Math.Abs(conversaId) % lista.Length)];
    }
}
