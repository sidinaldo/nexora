namespace Nexora.Core.Entidades;

// Espelham os enums nativos do Postgres (ver docs/SCHEMA-NEXORA.sql).
// O Npgsql traduz o nome do membro para snake_case: Vendedor -> 'vendedor'.
// NAO renomear membro sem alterar o enum no banco (ALTER TYPE ... RENAME VALUE).

/// <summary>Papel do usuario na equipe. dono = a empresa cliente (acesso total, gerencia
/// equipe e conexao); gestor = coordena a operacao; vendedor = atende e vende.</summary>
public enum PapelUsuario
{
    Dono,
    Gestor,
    Vendedor
}

/// <summary>Ciclo de vida do usuario. Tres estados, nao um booleano: 'convidado' ocupa
/// vaga mas ainda nao definiu senha, e 'inativo' e desligado e NAO ocupa vaga. Com um
/// booleano os dois seriam indistinguiveis, e a regra da tela de Equipe
/// ("vagas ocupadas = ativos + convidados") nao se escreve.</summary>
public enum StatusUsuario
{
    Ativo,
    Convidado,
    Inativo
}

/// <summary>Estado da instancia da Evolution.
///
/// 'offline' e distinto de 'desconectado' de proposito: offline = a Evolution API nao
/// respondeu (problema NOSSO, o cliente nao tem o que fazer); desconectado = a instancia
/// existe mas o numero caiu (o cliente precisa reparear). Colapsar os dois faz o painel
/// mandar escanear QR quando a culpa e da nossa infraestrutura.</summary>
public enum StatusConexao
{
    NaoCriada,
    Conectando,
    Conectado,
    Desconectado,
    Offline
}

/// <summary>De onde o lead veio. E a ORIGEM do contato, nao o canal de conversa: alguem que
/// viu um anuncio no Instagram e mandou mensagem no WhatsApp tem origem 'instagram'.</summary>
public enum OrigemLead
{
    Instagram,
    Facebook,
    Whatsapp,
    Google,
    Site,
    Qrcode,
    Indicacao,
    Manual,
    Outro
}

public enum DirecaoMensagem
{
    Entrada,
    Saida
}

public enum TipoMidia
{
    Nenhum,
    Imagem,
    Documento,
    Audio,
    Video
}

public enum StatusConversa
{
    Aberta,
    Resolvida
}

public enum StatusLembrete
{
    Pendente,
    Concluido,
    Cancelado
}

/// <summary>Quem criou o lembrete. So o 'automatico' dispara mensagem e entra no teto
/// diario anti-spam; o 'manual' e lembrete de acao para o vendedor (ligar, visitar).</summary>
public enum OrigemLembrete
{
    Automatico,
    Manual
}
