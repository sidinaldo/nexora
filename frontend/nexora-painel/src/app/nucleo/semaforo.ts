export type Urgencia = 'baixa' | 'media' | 'alta' | 'fora';

/** A janela de atendimento da empresa. Vem do servidor no /api/painel/status; o padrão abaixo
 *  só cobre o intervalo entre o boot da tela e a chegada da primeira resposta. */
export interface JanelaAtendimento {
  horaInicio: number;
  horaFim: number;
  /** Bitmask por dia da semana: bit 0 = domingo … bit 6 = sábado. 126 = seg a sáb. */
  diasSemana: number;
  /** Feriados relevantes ('YYYY-MM-DD'). Dia de feriado não conta como tempo de espera. */
  feriados: ReadonlySet<string>;
}

export const JANELA_PADRAO: JanelaAtendimento = {
  horaInicio: 8, horaFim: 20, diasSemana: 126, feriados: new Set<string>()
};

/** Monta a janela a partir do payload do painel. É o único lugar que traduz o contrato da API
 *  para o formato do cálculo — a página não desmonta o StatusPainel na mão. */
export function janelaDoStatus(s: {
  janelaHoraInicio: number; janelaHoraFim: number; janelaDiasSemana: number;
  feriadosRecentes?: string[];
}): JanelaAtendimento {
  return {
    horaInicio: s.janelaHoraInicio,
    horaFim: s.janelaHoraFim,
    diasSemana: s.janelaDiasSemana,
    // A API devolve DateOnly; o JSON pode trazer 'YYYY-MM-DD' ou o dia com hora colada.
    // Corta no 'T' para casar com a chave local gerada por `chaveDia`.
    feriados: new Set((s.feriadosRecentes ?? []).map(d => d.substring(0, 10)))
  };
}

/** A COR DO SEMÁFORO É CALCULADA AQUI, NO CLIENTE — nunca pedida ao servidor.
 *
 *  A razão é simples e não é preferência: a cor muda com o passar do tempo. Se o servidor
 *  mandasse "amarelo", a lista ficaria amarela até o próximo fetch, mesmo que a conversa já
 *  tivesse virado vermelha. A API manda o TIMESTAMP e os limites; a lista envelhece sozinha.
 *
 *  E a regra que o Recupera acertou: o alerta SÓ ACENDE dentro da janela de atendimento.
 *  Sem isso, o vendedor abre o sistema às 8h com tudo vermelho por mensagens que chegaram
 *  às 23h — e para de olhar para o semáforo, que é o único jeito de ele deixar de funcionar. */
export function urgenciaDe(
  aguardandoDesde: string | null,
  amareloMin: number,
  vermelhoMin: number,
  agora: Date = new Date(),
  janela: JanelaAtendimento = JANELA_PADRAO
): Urgencia {
  if (!aguardandoDesde) return 'baixa';
  if (!dentroDaJanela(agora, janela)) return 'fora';

  const minutos = minutosUteis(new Date(aguardandoDesde), agora, janela);
  if (minutos >= vermelhoMin) return 'alta';
  if (minutos >= amareloMin) return 'media';
  return 'baixa';
}

/** Chave local do dia — `toISOString()` NÃO serve: ela converte para UTC e, às 21h em Brasília,
 *  devolve o dia seguinte. O feriado seria descontado no dia errado. */
export function chaveDia(d: Date): string {
  const mes = `${d.getMonth() + 1}`.padStart(2, '0');
  const dia = `${d.getDate()}`.padStart(2, '0');
  return `${d.getFullYear()}-${mes}-${dia}`;
}

export function dentroDaJanela(quando: Date, janela: JanelaAtendimento): boolean {
  const diaLigado = (janela.diasSemana & (1 << quando.getDay())) !== 0;
  const horaOk = quando.getHours() >= janela.horaInicio && quando.getHours() < janela.horaFim;
  return diaLigado && horaOk && !janela.feriados.has(chaveDia(quando));
}

/** Minutos decorridos CONTANDO SÓ o tempo dentro da janela de atendimento.
 *
 *  Uma mensagem que chegou às 19h50 com janela até 20h tem 10 minutos de espera às 8h do dia
 *  seguinte — não 12 horas. Sem esse desconto, toda conversa da véspera aparece vermelha na
 *  primeira hora do expediente e o semáforo vira ruído.
 *
 *  ESPELHO de TempoUtil.MinutosUteis no servidor (Nexora.Core/Tempo). As duas implementações
 *  têm que dar o mesmo número: o Meu Dia ordena pelo cálculo do servidor e a caixa pinta pelo
 *  daqui — divergir faz a lista "pular" quando o vendedor troca de tela.
 *
 *  Percorre dia a dia; a trava de 400 iterações evita laço infinito se a janela vier zerada. */
export function minutosUteis(inicio: Date, fim: Date, janela: JanelaAtendimento): number {
  if (fim <= inicio) return 0;

  let total = 0;
  const cursor = new Date(inicio);

  for (let i = 0; i < 400 && cursor < fim; i++) {
    const diaLigado = (janela.diasSemana & (1 << cursor.getDay())) !== 0
      && !janela.feriados.has(chaveDia(cursor));

    if (diaLigado) {
      const abre = new Date(cursor); abre.setHours(janela.horaInicio, 0, 0, 0);
      const fecha = new Date(cursor); fecha.setHours(janela.horaFim, 0, 0, 0);

      const de = cursor > abre ? cursor : abre;
      const ate = fim < fecha ? fim : fecha;
      if (ate > de) total += (ate.getTime() - de.getTime()) / 60000;
    }

    // Próximo dia, na abertura.
    cursor.setDate(cursor.getDate() + 1);
    cursor.setHours(janela.horaInicio, 0, 0, 0);
  }

  return Math.floor(total);
}

/** "há 3h", "há 2d" — rótulo curto ao lado do ponto de urgência. */
export function rotuloEspera(aguardandoDesde: string | null, agora: Date = new Date()): string {
  if (!aguardandoDesde) return '';
  const min = Math.floor((agora.getTime() - new Date(aguardandoDesde).getTime()) / 60000);
  if (min < 1) return 'agora';
  if (min < 60) return `há ${min}min`;
  if (min < 60 * 24) return `há ${Math.floor(min / 60)}h`;
  return `há ${Math.floor(min / 1440)}d`;
}
