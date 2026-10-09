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

    /// <summary>Quantos números de WhatsApp esta empresa pode conectar.
    ///
    /// ===================== POR QUE UMA COLUNA, E NÃO UMA TABELA DE PLANOS =====================
    /// O limite vem do plano contratado, mas PLANO ainda não existe neste sistema — não há
    /// assinatura, cobrança nem catálogo. Criar a tabela agora seria modelar um domínio inteiro a
    /// partir de uma frase, e ela nasceria com uma linha e nenhum dono.
    ///
    /// O que o CÓDIGO precisa é do limite. Quem decide que o plano X dá três números é política
    /// comercial, e por enquanto vive fora do sistema. Quando planos existirem, esta coluna passa
    /// a ser derivada deles — e o ponto de enforcement (`ServicoConexoes.CriarAsync`) não muda.
    ///
    /// Padrão 1: era o que o índice único `uq_conexoes_empresa` impunha antes, e empresa que não
    /// contratou mais de um número continua com um.
    /// ========================================================================================</summary>
    public short LimiteConexoes { get; set; } = 1;

    /// <summary>O canal SUGERIDO ao criar uma conexao (INT-XX). So sugestao: cada conexao escolhe
    /// o seu, e uma empresa pode ter as duas.</summary>
    public CanalWhatsapp CanalPadrao { get; set; } = CanalWhatsapp.Evolution;

    /// <summary>===================== O TEMPLATE DE CADA AUTOMACAO (INT-XX) =====================
    ///
    /// Na API oficial, fora da janela de 24h so sai template aprovado. Follow-up, lembrete e NPS
    /// quase sempre saem DEPOIS de dias sem o cliente escrever — entao cada um tem o seu template,
    /// escolhido aqui. Nulo = sem template: com a janela fechada, a automatica nao sai, e o motivo
    /// fica registrado.
    ///
    /// Na Evolution nada disso se aplica: texto livre sai a qualquer hora.
    /// ==================================================================================</summary>
    public long? ModeloFollowUpId { get; set; }
    public long? ModeloLembreteId { get; set; }
    public long? ModeloNpsId { get; set; }

    /// <summary>Dias ate a venda ser concluida sozinha pela rodada diaria (NEG-2). Padrao 7.
    ///
    /// ZERO = concluir NA HORA. E o caso da padaria e do salao, que nao tem pendencia depois da
    /// venda — para eles a coluna Venda nunca deveria acumular um dia sequer.
    ///
    /// A automacao nao e conforto: vendedor nao gosta de tarefa administrativa, e SEM ELA a
    /// coluna volta a acumular em tres meses e o bloco nao resolveu nada.</summary>
    public short DiasParaConcluirVenda { get; set; } = 7;

    /// <summary>A rodada diaria pode concluir venda sozinha? (POS-1). Padrao LIGADO.
    ///
    /// ===================== POR QUE UMA COLUNA, E NAO UM VALOR EM `DiasParaConcluirVenda` =====================
    /// Nao havia como DESLIGAR. `0` ja significa "concluir na hora" e e valor legitimo (padaria,
    /// salao), 90 e "quase nunca" mas ainda conclui, e `NULL`/`-1` batem no
    /// `ck_empresas_conclusao BETWEEN 0 AND 90`. Sobrecarregar o numero com um terceiro
    /// significado custaria o CHECK e uma leitura anulavel em tres lugares.
    ///
    /// E guardar o numero enquanto a chave esta desligada e o que faz "religar" devolver o prazo
    /// que a empresa tinha, em vez de 7 por acidente.
    /// ======================================================================================
    ///
    /// LIGADO por padrao porque todo cliente que existe hoje conclui sozinho, e `false` pararia a
    /// rodada para todos sem ninguem notar por semanas.
    ///
    /// ⚠️ DESLIGAR TEM UM PRECO, e ele aparece na tela de Configuracoes: o card fica na coluna ate
    /// alguem clicar em Concluir, e enquanto estiver la o `uq_negociacoes_card_por_funil` impede
    /// aquele contato de abrir outro negocio no mesmo funil.</summary>
    public bool ConclusaoAutomatica { get; set; } = true;

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

    // ===================== A PESQUISA POS-VENDA (NPS-1) =====================

    /// <summary>⚠️ NASCE DESLIGADA, e nao e timidez: ligada por padrao, toda empresa existente
    /// comecaria a mandar mensagem automatica para os clientes dela no dia do deploy, sem ninguem
    /// ter escrito o texto nem escolhido o prazo. O dono liga quando decidir.</summary>
    public bool NpsAtivo { get; set; }

    /// <summary>Dias entre a conclusao da venda e a pergunta. ⚠️ TRES, e nao zero: perguntar no
    /// mesmo dia mede o atendimento, nao o PRODUTO — o cliente ainda nao usou o que comprou.
    /// Zero e valor legitimo para quem vende servico na hora.</summary>
    public short NpsDiasAposConclusao { get; set; } = 3;

    /// <summary>Dias esperando a nota antes de desistir. ⚠️ SEM REENVIO depois disso: quem nao
    /// respondeu em tres dias nao responde ao quarto lembrete, e insistir num numero de WhatsApp
    /// e o jeito classico de ser bloqueado.</summary>
    public short NpsDiasExpiracao { get; set; } = 3;

    /// <summary>A pergunta. `{{saudacao}}`, `{{nome}}` e `{{empresa}}` sao substituidos no envio.
    ///
    /// ⚠️ O TEXTO PEDE O NUMERO EXPLICITAMENTE ("e so responder com o numero"), e isso nao e
    /// enfeite: `LeitorDeNota` so reconhece a nota com confianca quando ela vem sozinha, depois
    /// de um puxador, ou fechada por pontuacao. Uma pergunta que convide a prosa produziria
    /// `PossivelNota` em serie, e cada uma dessas e trabalho manual para o vendedor.
    ///
    /// ===================== POR QUE `{{saudacao}}` E NAO "Oi, {{nome}}!" =====================
    /// O prompt pedia `"Oi, {{nome}}! ..."` como padrao, e esse texto tem um defeito que este
    /// projeto JA CONSERTOU UMA VEZ e documentou em `NomeDePessoa`: quando o WhatsApp nao manda
    /// `pushName`, o `CanonicalizadorTelefone` vira o NOME do contato, e `Primeiro` devolve NULO
    /// de proposito — "(84)" nao e um nome. Substituir `{{nome}}` por vazio produz:
    ///
    ///     "Oi, ! Aqui é da Padaria. De 0 a 10..."
    ///
    /// ⚠️ MEDIDO NO `nexora_dev`: 4 dos 1250 contatos (0,3%) tem telefone no lugar do nome. Pouca
    /// gente, e cada uma receberia isso no WhatsApp dela.
    ///
    /// `NomeDePessoa.Saudacao` resolve porque decide a PONTUACAO junto com o nome: "Oi, Maria!" ou
    /// "Oi!". `{{nome}}` continua disponivel para o dono que escrever o proprio texto — a ele cabe
    /// a escolha —, mas o PADRAO nao pode nascer com essa armadilha.
    /// ======================================================================================</summary>
    public string NpsTexto { get; set; } =
        "{{saudacao}} Aqui é da {{empresa}}. De 0 a 10, quanto você recomendaria a gente para "
        + "um amigo? É só responder com o número.";

    /// <summary>Agradecimento para nota 9-10. VAZIO = nao envia, e e o padrao: uma segunda
    /// mensagem automatica depois da primeira dobra o risco do numero, e nem toda empresa quer.</summary>
    public string? NpsMensagemPromotor { get; set; }

    /// <summary>Resposta para nota 0-6. VAZIO = nao envia. ⚠️ O detrator gera lembrete para o
    /// responsavel e aviso para o dono DE QUALQUER JEITO — a mensagem automatica e opcional, a
    /// acao humana nao.</summary>
    public string? NpsMensagemDetrator { get; set; }

    /// <summary>O resumo do dia anterior por e-mail, para o dono, na rodada das 8h (RES-XX).
    /// DESLIGADO por padrao: e-mail que ninguem pediu vira caixa de spam, e o dono liga quando quer.</summary>
    public bool ResumoDiarioAtivo { get; set; }

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

    /// <summary>O dono disse "não vou conectar meus anúncios" (INT-4). Mesma natureza das duas
    /// acima, e o mesmo carimbo idempotente.
    ///
    /// ⚠️ O PASSO DE ANÚNCIOS SÓ APARECE QUANDO HÁ ANÚNCIO CHEGANDO, então esta coluna é para
    /// quem ANUNCIA e mesmo assim não quer a integração — um caso legítimo, e o único em que o
    /// passo ficaria aceso para sempre sem ela.</summary>
    public DateTime? AnunciosDispensadosEm { get; set; }

    /// <summary>O dono fechou o painel de primeiros passos. Mesma natureza da anterior:
    /// registra uma escolha que nenhuma consulta consegue inferir.</summary>
    public DateTime? OnboardingDispensadoEm { get; set; }

    /// <summary>Em qual plano do catálogo esta empresa está. ⚠️ É RÓTULO, não regra: quem manda
    /// são `LimiteConexoes` e `LimiteUsuarios` nesta linha. Ver <see cref="Plano"/> para por que o
    /// plano é molde e não ponteiro vivo.
    ///
    /// ⚠️ NULL NÃO É "ILIMITADO" NEM "BLOQUEADO". É "ninguém registrou qual plano", e os limites
    /// desta linha valem igual. Toda empresa anterior a esta coluna está assim, e isso é honesto —
    /// inventar um plano no backfill faria o histórico do que foi vendido começar com uma ficção.</summary>
    public long? PlanoId { get; set; }
    public Plano? Plano { get; set; }

    /// <summary>Quantas pessoas a empresa pode ter no painel.
    ///
    /// ===================== O QUE OCUPA VAGA =====================
    /// `ativo` + `convidado`. `inativo` NÃO ocupa. A regra não nasceu aqui: está em
    /// `docs/SCHEMA-NEXORA.sql`, escrita quando o status virou enum de três valores e limite nenhum
    /// existia — *"'convidado' é vaga ocupada mas sem senha definida; 'inativo' é desligado e NÃO
    /// ocupa vaga"*.
    ///
    /// ⚠️ O CONVITE PENDENTE OCUPA VAGA, E TEM DE OCUPAR. A alternativa seria cobrar no aceite — e
    /// aí alguém convidado na segunda, com o limite baixado na terça, clica o link na quarta e é
    /// barrado, sem nenhuma ação possível do lado dele. Reservar no convite é o que garante que o
    /// ACEITE NUNCA FALHA.
    ///
    /// Padrão 3 no BANCO, não só no C#: o dono mais duas pessoas. Teto 50 é freio de digitação,
    /// igual ao 20 de conexões; piso 1 porque toda empresa tem ao menos o dono, e um 0 tornaria a
    /// linha dele ilegal.
    /// ============================================================</summary>
    public short LimiteUsuarios { get; set; } = 3;

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public ICollection<Usuario> Usuarios { get; set; } = [];
}
