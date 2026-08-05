namespace Nexora.Core.Entidades;

/// <summary>Pessoa que usa o painel. Pertence a uma empresa (tenant).</summary>
public class Usuario : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public string Nome { get; set; } = null!;
    public string Email { get; set; } = null!;

    /// <summary>Formato pbkdf2$iteracoes$salt$hash (ver HashSenha).
    ///
    /// NULL enquanto o convite nao foi aceito — o convidado so define a senha no aceite.
    /// E por isso que a coluna e anulavel: um NOT NULL obrigaria a gravar um hash falso
    /// no convite e destruiria a checagem que distingue "convidado sem senha" de
    /// "senha errada" no login. O banco garante a coerencia com o check ck_usuarios_senha.</summary>
    public string? SenhaHash { get; set; }

    public PapelUsuario Papel { get; set; }
    public StatusUsuario Status { get; set; } = StatusUsuario.Ativo;

    /// <summary>Bloqueio PERSISTENTE por conta, complementar ao rate limit por IP: N falhas
    /// seguidas travam a conta por M minutos, cross-IP. Ver PoliticaLogin.</summary>
    public short FalhasLogin { get; set; }
    public DateTime? BloqueadoAte { get; set; }

    /// <summary>Convite e redefinicao de senha tem colunas SEPARADAS de proposito. Reusar as
    /// mesmas para os dois fluxos (como o Recupera faz) confunde o caso do usuario convidado
    /// que pede reset antes de aceitar o convite.</summary>
    public string? TokenConvite { get; set; }
    public DateTime? ConviteExpira { get; set; }
    public string? TokenReset { get; set; }
    public DateTime? ResetExpira { get; set; }

    public DateTime? UltimoAcessoEm { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
