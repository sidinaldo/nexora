import { IRetryPolicy, RetryContext } from '@microsoft/signalr';

/** ===================== O TEMPO REAL NÃO PODE DESISTIR =====================
 *
 *  `withAutomaticReconnect()` sem argumento usa a política padrão do SignalR: tenta em 0s, 2s,
 *  10s e 30s — e **desiste**. Depois disso `onclose` dispara e a conexão fica morta até alguém
 *  recarregar a página.
 *
 *  Quarenta segundos é menos que um restart de API. Em desenvolvimento isso acontece a cada
 *  compilação; em produção, a cada deploy. O sintoma é sempre o mesmo e sempre silencioso: o
 *  vendedor continua com o painel aberto, as mensagens continuam chegando no banco, e a tela
 *  não mexe. Só descobre quem troca de tela e volta.
 *
 *  Esta política NUNCA devolve `null`. O backoff cresce até um teto e fica lá: se o servidor
 *  voltar em duas horas, a próxima tentativa o encontra.
 *  ====================================================================== */
export class PoliticaReconexao implements IRetryPolicy {
  /** Os primeiros intervalos, em ms. Rápido no começo porque a causa mais comum é um restart
   *  de API que leva poucos segundos. */
  private static readonly DEGRAUS = [0, 1_000, 2_000, 5_000, 10_000, 20_000];

  /** O teto. Meio minuto é frequente o bastante para o vendedor não perceber a volta, e raro o
   *  bastante para não martelar um servidor que está fora do ar de verdade. */
  static readonly TETO = 30_000;

  nextRetryDelayInMilliseconds(ctx: RetryContext): number {
    return PoliticaReconexao.DEGRAUS[ctx.previousRetryCount] ?? PoliticaReconexao.TETO;
  }
}

/** O mesmo escalonamento para a tentativa INICIAL, que o SignalR não cobre.
 *
 *  `withAutomaticReconnect` só age sobre uma conexão que chegou a subir. Se o primeiro `start()`
 *  falha — a API ainda não subiu, a rede piscou, o token ainda não estava lá — não existe
 *  reconexão automática nenhuma: é o caso em que o painel abre já sem tempo real. */
export function esperaDaTentativa(tentativa: number): number {
  return new PoliticaReconexao().nextRetryDelayInMilliseconds(
    { previousRetryCount: tentativa, elapsedMilliseconds: 0, retryReason: new Error() });
}
