import { JanelaWhatsapp } from './modelos';

/** ===================== A JANELA DO WHATSAPP NA TELA (INT-XX) =====================
 *
 *  Na API oficial da Meta, texto livre só sai nas 24h depois da última mensagem do cliente; fora
 *  disso, só template aprovado. O servidor manda os instantes prontos (`JanelaWhatsapp`) — a regra
 *  é dele — e aqui só se compara com o relógio, como o semáforo faz com a espera.
 *
 *  ⚠️ NÃO É A `JanelaAtendimento` do semáforo. Aquela é o horário comercial da empresa; esta é da
 *  Meta. Por isso o selo diz "janela do WhatsApp" por extenso no `title`.
 *
 *  Na Evolution nada bloqueia: o selo aparece atenuado, como informação de tempo de resposta.
 *  ================================================================================ */
export type EstadoJanela = 'aberta' | 'fechando' | 'fechada';

export interface SituacaoJanela {
  estado: EstadoJanela;
  /** O texto curto do selo: "janela 5h", "fecha em 40 min", "janela fechada". */
  rotulo: string;
  /** A explicação, para o `title`. */
  titulo: string;
  /** Só na API oficial. Na Evolution o selo é informação, e fica atenuado. */
  bloqueia: boolean;
  /** A cor do selo, com as classes que o tema já tem: na API oficial, a do estado; na Evolution,
   *  nenhuma — o `.selo` neutro. */
  classe: '' | 'selo-ok' | 'selo-atencao' | 'selo-perigo';
}

export function situacaoDaJanela(
  janela: JanelaWhatsapp | null | undefined, agora: Date
): SituacaoJanela | null {
  if (!janela) return null;

  const situacao = calcular(janela, agora);
  return { ...situacao, classe: classeDe(situacao.estado, situacao.bloqueia) };
}

function classeDe(estado: EstadoJanela, bloqueia: boolean): SituacaoJanela['classe'] {
  if (!bloqueia) return '';
  if (estado === 'aberta') return 'selo-ok';
  if (estado === 'fechando') return 'selo-atencao';
  return 'selo-perigo';
}

function calcular(janela: JanelaWhatsapp, agora: Date): Omit<SituacaoJanela, 'classe'> {
  const bloqueia = janela.bloqueia;
  const fecha = janela.fechaEm ? new Date(janela.fechaEm) : null;

  if (fecha === null) {
    return {
      estado: 'fechada', rotulo: 'Janela fechada', bloqueia,
      titulo: bloqueia
        ? 'O cliente ainda não escreveu para este número. Pela API oficial, só template aprovado pode ser enviado.'
        : 'O cliente ainda não escreveu para este número.'
    };
  }

  if (agora.getTime() >= fecha.getTime()) {
    return {
      estado: 'fechada', rotulo: 'Janela fechada', bloqueia,
      titulo: bloqueia
        ? 'Passaram 24h desde a última mensagem do cliente. Pela API oficial, só template aprovado pode ser enviado.'
        : 'Passaram 24h desde a última mensagem do cliente. Neste número isso não bloqueia nada — é só o tempo de resposta.'
    };
  }

  const minutos = Math.ceil((fecha.getTime() - agora.getTime()) / 60_000);
  const hora = fecha.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  const aviso = janela.avisoEm ? new Date(janela.avisoEm) : null;

  if (aviso !== null && agora.getTime() >= aviso.getTime()) {
    return {
      estado: 'fechando', rotulo: 'Fecha em ' + duracao(minutos, true), bloqueia,
      titulo: bloqueia
        ? `A janela do WhatsApp fecha às ${hora}. Depois disso, pela API oficial, só template aprovado.`
        : `A janela do WhatsApp fecha às ${hora}. Neste número isso não bloqueia nada.`
    };
  }

  return {
    estado: 'aberta', rotulo: 'Janela ' + duracao(minutos, false), bloqueia,
    titulo: `Janela do WhatsApp aberta: o cliente escreveu nas últimas 24h. Fecha às ${hora}.`
  };
}

/** "40 min", "5h" — ou, perto de fechar, "1h20": nas duas últimas horas o minuto importa. */
function duracao(minutos: number, comMinutos: boolean): string {
  if (minutos < 60) return `${minutos} min`;

  const horas = Math.floor(minutos / 60);
  const resto = minutos % 60;
  if (comMinutos && resto > 0) return `${horas}h${String(resto).padStart(2, '0')}`;
  return `${horas}h`;
}
