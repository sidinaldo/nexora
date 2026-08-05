namespace Nexora.Core.Entidades;

/// <summary>O TENANT: a empresa cliente do Nexora (uma PME que vende por WhatsApp).
/// Todo o isolamento gira em torno do Id dela.</summary>
public class Empresa : IEntidadeAuditada
{
    public long Id { get; set; }
    public string Nome { get; set; } = null!;

    /// <summary>CNPJ ou CPF, so digitos (sem mascara).</summary>
    public string? Documento { get; set; }

    /// <summary>Portao de LOGIN: empresa inativa nao autentica ninguem.</summary>
    public bool Ativo { get; set; } = true;

    /// <summary>Tenant de DEMONSTRAÇÃO — dados semeados, nenhuma pessoa real do outro lado.
    ///
    /// ===================== O QUE ESTA COLUNA DESLIGA =====================
    /// Empresa marcada NÃO entra na rodada do `MotorFollowUp` e NÃO consegue disparar mensagem
    /// pelo `EnviadorMensagem`. Sem isso, um tenant de demonstração pareado a uma instância real
    /// da Evolution mandaria follow-up automático para os telefones semeados.
    ///
    /// É a segunda de três barreiras (a faixa de números é a primeira, a recusa no envio é a
    /// terceira). Falso por padrão: empresa que não pediu para ser demonstração não é.
    /// =====================================================================</summary>
    public bool Demonstracao { get; set; }

    /// <summary>Janela de atendimento (horario comercial). Governa tres coisas nos blocos
    /// seguintes: quando o lembrete automatico pode disparar, quando o semaforo de urgencia
    /// acende (para nao piscar de madrugada) e o que o "Meu Dia" mostra.</summary>
    public short JanelaHoraInicio { get; set; } = 8;
    public short JanelaHoraFim { get; set; } = 20;

    /// <summary>Bitmask por DayOfWeek do .NET: Dom=bit0 .. Sab=bit6. 126 = seg a sab.
    /// Bitmask em vez de tabela porque e lido em todo calculo de janela, e uma coluna
    /// evita join no caminho quente.</summary>
    public short JanelaDiasSemana { get; set; } = 126;

    /// <summary>A janela e comparada contra "agora no fuso de negocio". Deixar o fuso numa
    /// constante da aplicacao erra 1-2h para cliente em Manaus ou Rio Branco; coluna por
    /// tenant custa nada agora e evita migracao depois.</summary>
    public string FusoHorario { get; set; } = "America/Sao_Paulo";

    /// <summary>UF da empresa (sigla de dois caracteres), para semear os feriados ESTADUAIS.
    ///
    /// Nullable porque empresa cadastrada antes desta coluna não tem UF, e exigir um valor
    /// obrigaria a inventar um. Sem UF, a empresa recebe só os feriados nacionais — que é o
    /// comportamento de hoje, e continua correto.</summary>
    public string? Uf { get; set; }

    /// <summary>Quantos dias de conversa parada disparam o follow-up automático.
    ///
    /// Conta a partir da ULTIMA MENSAGEM, e só quando ela foi de SAIDA — se a última foi de
    /// entrada, o cliente está esperando resposta, e isso é semáforo, não follow-up.</summary>
    public short DiasSemRespostaFollowUp { get; set; } = 2;

    /// <summary>Faixas do semáforo, em minutos ÚTEIS (descontando o que está fora do expediente).
    /// Abaixo de amarelo = verde; entre os dois = amarelo; acima de vermelho = vermelho.
    ///
    /// Vão para o cliente no /api/painel/status: quem PINTA é o navegador, porque a cor precisa
    /// envelhecer entre requisições.</summary>
    public short SemaforoAmareloMinutos { get; set; } = 60;
    public short SemaforoVermelhoMinutos { get; set; } = 240;

    /// <summary>Quando a PRIMEIRA mensagem de entrada chegou. Gravado UMA vez, pelo webhook.
    ///
    /// ===================== O TEMPO ATÉ O VALOR =====================
    /// `primeira_mensagem_em - criado_em` é o intervalo entre a empresa assinar e o produto
    /// funcionar de verdade pela primeira vez. É a métrica que prevê abandono melhor que
    /// qualquer outra: quem passa dias sem receber a primeira mensagem não voltou a tentar.
    ///
    /// Fica MATERIALIZADA em vez de derivada de `MIN(mensagens.recebida_em)` porque é lida
    /// junto do checklist de onboarding, a cada carregamento da tela — e um MIN sobre a tabela
    /// de maior escrita do sistema, por empresa, não é leitura de tela.
    ///
    /// ⚠️ NÃO é promessa de prazo. Pareamento por QR, persistência de sessão e reconexão da
    /// Evolution não obedecem cronômetro; este número é para OLHAR, nunca para prometer.
    /// ==============================================================</summary>
    public DateTime? PrimeiraMensagemEm { get; set; }

    /// <summary>O dono disse "convido a equipe depois". Decisão de PESSOA, não estado do
    /// sistema — por isso é guardada, ao contrário dos passos do checklist, que são derivados.</summary>
    public DateTime? EquipeDispensadaEm { get; set; }

    /// <summary>O dono fechou o painel de primeiros passos. Mesma natureza da anterior:
    /// registra uma escolha que nenhuma consulta consegue inferir.</summary>
    public DateTime? OnboardingDispensadoEm { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public ICollection<Usuario> Usuarios { get; set; } = [];
}
