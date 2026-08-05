using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

public class ServicoAutenticacao(
    NexoraDbContext db,
    TimeProvider relogio,
    ILogger<ServicoAutenticacao> log) : IServicoAutenticacao
{
    public async Task<UsuarioAutenticado?> AutenticarAsync(string email, string senha, CancellationToken ct)
    {
        // ===================== A ARMADILHA =====================
        // O login roda SEM tenant no contexto: ainda nao sabemos de qual empresa e este
        // usuario — e exatamente isso que estamos descobrindo. Sem IgnoreQueryFilters(),
        // o filtro global compara EmpresaId com 0, a consulta volta VAZIA em silencio
        // (sem erro nenhum!) e o login nunca autentica ninguem.
        //
        // Funciona porque o e-mail e unico GLOBALMENTE (uq_usuarios_email em lower(email)),
        // nao por empresa. Se fosse por empresa, dois tenants com o mesmo e-mail fariam o
        // login autenticar num deles ARBITRARIAMENTE.
        //
        // Aqui o IgnoreQueryFilters e seguro SEM um Where por empresaId porque o proprio
        // e-mail e a chave global. Nos demais caminhos sem tenant (webhook, job), ele
        // PRECISA vir acompanhado do filtro explicito — senao a consulta varre os tenants.
        var usuario = await db.Usuarios.IgnoreQueryFilters()
            .Include(u => u.Empresa)
            .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), ct);

        var agora = relogio.GetUtcNow().UtcDateTime;

        // Bloqueio PERSISTENTE por conta (alem do rate limit por janela): apos N falhas seguidas a
        // conta trava por 15 min, cross-IP. Nem chega a checar a senha. Roda o hash dummy mesmo
        // assim, para nao denunciar por timing que a conta esta bloqueada.
        if (usuario is { BloqueadoAte: { } ate } && ate > agora)
        {
            HashSenha.Confere(senha, PoliticaLogin.HashDummy);
            log.LogWarning("Login barrado (conta bloqueada) para {Email}.", PoliticaLogin.MascararEmail(email));
            return null;
        }

        // Sem usuario ou convidado sem senha: gasta o MESMO tempo de um verify real (hash dummy) e
        // sai. Mesma resposta generica de "senha errada" — nao revela quais e-mails existem.
        if (usuario is null || usuario.SenhaHash is null)
        {
            HashSenha.Confere(senha, PoliticaLogin.HashDummy);
            log.LogWarning("Login negado (inexistente/sem senha) para {Email}.", PoliticaLogin.MascararEmail(email));
            return null;
        }

        // Senha errada: conta a falha e, no teto, tranca a conta (zerando o contador para dar
        // novas tentativas depois do bloqueio).
        if (!HashSenha.Confere(senha, usuario.SenhaHash))
        {
            usuario.FalhasLogin++;
            if (usuario.FalhasLogin >= PoliticaLogin.MaxFalhasConsecutivas)
            {
                usuario.BloqueadoAte = agora.Add(PoliticaLogin.Bloqueio);
                usuario.FalhasLogin = 0;
            }
            await db.SaveChangesAsync(ct);
            log.LogWarning("Login negado (senha) para {Email}.", PoliticaLogin.MascararEmail(email));
            return null;
        }

        if (usuario.Status != StatusUsuario.Ativo)
            throw new RegraDeNegocioException("Usuario desativado. Fale com o dono da conta.");

        // O portao de LOGIN do SaaS: empresa inativa nao autentica ninguem.
        if (!usuario.Empresa.Ativo)
            throw new RegraDeNegocioException("Empresa inativa. Fale com o suporte.");

        // Login OK: zera o contador de falhas e o bloqueio.
        usuario.FalhasLogin = 0;
        usuario.BloqueadoAte = null;
        usuario.UltimoAcessoEm = agora;
        await db.SaveChangesAsync(ct);

        return new UsuarioAutenticado(
            usuario.Id, usuario.Nome, usuario.Email, usuario.Papel.ToString().ToLowerInvariant(),
            usuario.EmpresaId, usuario.Empresa.Nome);
    }
}
