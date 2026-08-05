import {
  JANELA_PADRAO, JanelaAtendimento, chaveDia, dentroDaJanela, janelaDoStatus,
  minutosUteis, rotuloEspera, urgenciaDe
} from './semaforo';

/** Janela de teste: 8h–20h, segunda a sábado (bitmask 126), sem feriado. */
function janela(over: Partial<JanelaAtendimento> = {}): JanelaAtendimento {
  return { horaInicio: 8, horaFim: 20, diasSemana: 126, feriados: new Set<string>(), ...over };
}

// Hora de parede, sem zona — igual aos casos de paridade. Ver semaforo.paridade.spec.ts.
// 06/08/2026 é quinta; 09/08 é domingo.
const QUINTA_10H = new Date('2026-08-06T10:00:00');
const QUINTA_23H = new Date('2026-08-06T23:00:00');
const SEXTA_08H = new Date('2026-08-07T08:00:00');
const DOMINGO_10H = new Date('2026-08-09T10:00:00');

describe('chaveDia', () => {
  it('formata com zero à esquerda no mês e no dia', () => {
    expect(chaveDia(new Date('2026-01-05T10:00:00'))).toBe('2026-01-05');
    expect(chaveDia(new Date('2026-12-31T10:00:00'))).toBe('2026-12-31');
  });

  it('NÃO usa toISOString: às 21h em Brasília o ISO devolve o dia seguinte', () => {
    // ===================== O TESTE QUE IMPEDE A "SIMPLIFICAÇÃO" =====================
    // `toISOString()` converte para UTC. Às 21h em Brasília (UTC-3) isso é 00h do dia
    // seguinte — e o feriado seria descontado no dia errado, silenciosamente.
    //
    // O caso é montado com um objeto que responde os getters LOCAIS de um instante em UTC-3,
    // e não com um Date real, DE PROPÓSITO: num runner em UTC (que é o caso do CI) hora local
    // e UTC coincidem, a discrepância não existe, e um teste com Date real passaria com
    // qualquer das duas implementações — verde exatamente onde precisava morder.
    //
    // Assim o teste vale em qualquer fuso, inclusive no do CI.
    // ================================================================================
    const vinteEUmaEmBrasilia = {
      getFullYear: () => 2026,
      getMonth: () => 7,      // agosto (base zero)
      getDate: () => 6,       // quinta, 06/08 — o dia LOCAL
      toISOString: () => '2026-08-07T00:00:00.000Z'   // já é dia 7 em UTC
    } as unknown as Date;

    expect(chaveDia(vinteEUmaEmBrasilia)).toBe('2026-08-06');

    // E a armadilha, explícita: quem trocar o corpo por toISOString() devolve o dia errado.
    expect(vinteEUmaEmBrasilia.toISOString().substring(0, 10)).toBe('2026-08-07');
    expect(chaveDia(vinteEUmaEmBrasilia))
      .not.toBe(vinteEUmaEmBrasilia.toISOString().substring(0, 10));
  });

  it('sempre concorda com os getters locais, em qualquer instante', () => {
    // A propriedade geral: a chave é a data LOCAL. Num runner fora de UTC, este teste sozinho
    // já reprova toISOString().
    for (const iso of ['2026-08-06T00:00:00', '2026-08-06T21:00:00', '2026-12-31T23:59:59']) {
      const d = new Date(iso);
      const esperado =
        `${d.getFullYear()}-${`${d.getMonth() + 1}`.padStart(2, '0')}-${`${d.getDate()}`.padStart(2, '0')}`;
      expect(chaveDia(d)).toBe(esperado);
    }
  });
});

describe('dentroDaJanela', () => {
  it('aceita hora dentro do expediente em dia ligado', () => {
    expect(dentroDaJanela(QUINTA_10H, janela())).toBeTrue();
  });

  it('recusa antes da abertura e a partir do fechamento', () => {
    expect(dentroDaJanela(new Date('2026-08-06T07:59:00'), janela())).toBeFalse();
    // 20h é o fim: a comparação é `< horaFim`, então as 20h já estão fora.
    expect(dentroDaJanela(new Date('2026-08-06T20:00:00'), janela())).toBeFalse();
    expect(dentroDaJanela(new Date('2026-08-06T19:59:00'), janela())).toBeTrue();
  });

  it('recusa dia desligado no bitmask', () => {
    expect(dentroDaJanela(DOMINGO_10H, janela())).toBeFalse();
  });

  it('recusa feriado mesmo em dia e hora válidos', () => {
    const comFeriado = janela({ feriados: new Set(['2026-08-06']) });
    expect(dentroDaJanela(QUINTA_10H, comFeriado)).toBeFalse();
    // E o feriado é do DIA certo: o dia seguinte continua valendo.
    expect(dentroDaJanela(new Date('2026-08-07T10:00:00'), comFeriado)).toBeTrue();
  });
});

describe('minutosUteis', () => {
  it('conta só o tempo dentro da janela', () => {
    expect(minutosUteis(QUINTA_10H, new Date('2026-08-06T11:30:00'), janela())).toBe(90);
  });

  it('desconta a noite: 19h50 tem 10 minutos às 8h do dia seguinte', () => {
    expect(minutosUteis(new Date('2026-08-06T19:50:00'), SEXTA_08H, janela())).toBe(10);
  });

  it('devolve zero quando o fim não é depois do início', () => {
    expect(minutosUteis(new Date('2026-08-06T11:00:00'), QUINTA_10H, janela())).toBe(0);
    expect(minutosUteis(QUINTA_10H, QUINTA_10H, janela())).toBe(0);
  });

  it('não trava com bitmask zerado — a trava de 400 iterações segura', () => {
    // Dado ruim (nenhum dia ligado) faria o laço rodar para sempre sem a trava. Se alguém a
    // remover, este teste não falha: ele PENDURA a suíte, que é o sintoma que se quer ver.
    const inicio = new Date('2026-01-01T08:00:00');
    const fim = new Date('2030-01-01T08:00:00');
    expect(minutosUteis(inicio, fim, janela({ diasSemana: 0 }))).toBe(0);
  });
});

describe('urgenciaDe', () => {
  it('sem espera registrada é baixa', () => {
    expect(urgenciaDe(null, 30, 120, QUINTA_10H, janela())).toBe('baixa');
  });

  it('FORA DA JANELA NÃO ACENDE — a regra que faz o semáforo continuar sendo olhado', () => {
    // ===================== POR QUE ISTO IMPORTA =====================
    // Sem esta regra, o vendedor abre o sistema às 8h com a tela inteira vermelha por
    // mensagens que chegaram às 23h e ninguém poderia ter respondido. Um alerta que sempre
    // acende deixa de ser alerta — é a única forma de o semáforo parar de funcionar.
    // ===============================================================
    const madrugada = new Date('2026-08-07T03:00:00');
    expect(urgenciaDe('2026-08-06T23:00:00', 30, 120, madrugada, janela())).toBe('fora');
    expect(urgenciaDe('2026-08-06T10:00:00', 30, 120, DOMINGO_10H, janela())).toBe('fora');
  });

  it('a mensagem das 23h não chega vermelha às 8h do dia seguinte', () => {
    // O caso concreto da regra acima: às 8h já estamos DENTRO da janela, mas o tempo noturno
    // não conta — a espera útil é ~0, não 9 horas.
    expect(urgenciaDe(QUINTA_23H.toISOString(), 30, 120, SEXTA_08H, janela())).toBe('baixa');
  });

  it('escala baixa -> media -> alta pelos limites recebidos', () => {
    const desde = '2026-08-06T10:00:00';
    expect(urgenciaDe(desde, 30, 120, new Date('2026-08-06T10:29:00'), janela())).toBe('baixa');
    expect(urgenciaDe(desde, 30, 120, new Date('2026-08-06T10:30:00'), janela())).toBe('media');
    expect(urgenciaDe(desde, 30, 120, new Date('2026-08-06T11:59:00'), janela())).toBe('media');
    expect(urgenciaDe(desde, 30, 120, new Date('2026-08-06T12:00:00'), janela())).toBe('alta');
  });
});

describe('janelaDoStatus', () => {
  it('traduz o payload do painel e corta a hora colada na data do feriado', () => {
    // A API devolve DateOnly; o JSON pode vir 'YYYY-MM-DD' ou com hora junto. A chave tem que
    // casar com a que `chaveDia` gera, senão o feriado nunca é encontrado.
    const j = janelaDoStatus({
      janelaHoraInicio: 9, janelaHoraFim: 18, janelaDiasSemana: 62,
      feriadosRecentes: ['2026-08-06T00:00:00', '2026-09-07']
    });

    expect(j.horaInicio).toBe(9);
    expect(j.horaFim).toBe(18);
    expect(j.diasSemana).toBe(62);
    expect(j.feriados.has('2026-08-06')).toBeTrue();
    expect(j.feriados.has('2026-09-07')).toBeTrue();
  });

  it('aguenta feriados ausentes no payload', () => {
    const j = janelaDoStatus({ janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126 });
    expect(j.feriados.size).toBe(0);
  });
});

describe('rotuloEspera', () => {
  it('muda de unidade conforme a espera cresce', () => {
    const agora = new Date('2026-08-06T12:00:00');
    expect(rotuloEspera(null, agora)).toBe('');
    expect(rotuloEspera('2026-08-06T11:59:30', agora)).toBe('agora');
    expect(rotuloEspera('2026-08-06T11:15:00', agora)).toBe('há 45min');
    expect(rotuloEspera('2026-08-06T09:00:00', agora)).toBe('há 3h');
    expect(rotuloEspera('2026-08-04T12:00:00', agora)).toBe('há 2d');
  });
});

describe('JANELA_PADRAO', () => {
  it('cobre o intervalo entre o boot da tela e a resposta do servidor', () => {
    // Não é configuração: é só o que vale até o /api/painel/status chegar. Se virar 0, a tela
    // pintaria tudo como "fora" no primeiro segundo.
    expect(JANELA_PADRAO.horaInicio).toBe(8);
    expect(JANELA_PADRAO.horaFim).toBe(20);
    expect(JANELA_PADRAO.diasSemana).toBe(126);
  });
});
