using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nexora.Tests.Integracao;

/// <summary>Faz FALHAR o comando SQL cujo texto contem `Trecho`, quando armado.
///
/// Existe para provar ATOMICIDADE: "ou mudam as tres colunas, ou nenhuma" dito num comentario nao
/// vale nada — o comentario do `RedistribuirAsync` dizia "na mesma transacao" e nao havia
/// transacao nenhuma. Com isto o teste derruba a TERCEIRA escrita e confere que a primeira voltou.
///
/// Comeca DESARMADO: a preparacao do teste usa o mesmo contexto e tocaria a mesma tabela.</summary>
public sealed class FalhaNoComando(string trecho) : DbCommandInterceptor
{
    public bool Armada { get; set; }

    /// <summary>Quantas vezes falhar, no maximo; nulo = todas. Para provar que a falha de UM item
    /// nao contamina o SEGUINTE — o primeiro falha, o segundo passa.</summary>
    public int? Limite { get; set; }

    /// <summary>O que lancar. Nulo = `InvalidOperationException`. Existe para simular o erro de
    /// verdade do banco — o indice unico barrando a corrida (BUG-XX).</summary>
    public Func<Exception>? Excecao { get; init; }

    private int _falhas;

    private void Conferir(DbCommand comando)
    {
        if (!Armada) return;
        if (Limite != null && _falhas >= Limite) return;

        // Sem aspas: o EF pode citar o identificador ou nao, e `UPDATE conversas` tem de casar
        // nos dois casos — no `SaveChanges` e no `ExecuteUpdate` (`UPDATE conversas AS c`).
        if (comando.CommandText.Replace("\"", "").Contains(trecho, StringComparison.OrdinalIgnoreCase))
        {
            _falhas++;
            throw Excecao?.Invoke()
                ?? new InvalidOperationException($"Falha simulada no comando que toca \"{trecho}\".");
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Conferir(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Conferir(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
