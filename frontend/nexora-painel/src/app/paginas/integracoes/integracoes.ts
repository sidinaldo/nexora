import {
  Component, ElementRef, OnInit, ViewChild, computed, inject, signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import {
  Paginacao, alturaMinimaDaTabela, fatiar, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { WebhooksServico } from '../../nucleo/servicos/webhooks.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import {
  EntregaWebhookDto, EventoWebhook, ResultadoTeste, SegredoRevelado, WebhookDto
} from '../../nucleo/modelos';

/** INTEGRAÇÕES — o webhook de saída.
 *
 *  ===================== O QUE ESTA TELA PRECISA ENTREGAR =====================
 *  Três coisas, e a ordem importa:
 *
 *  1. **O botão de teste.** Ele resolve a maior parte dos chamados sozinho: o dono configura, clica,
 *     e sabe na hora se o servidor dele respondeu — em vez de criar um lead de mentira e esperar.
 *
 *  2. **Como validar a assinatura.** Assinatura que ninguém valida é enfeite. O snippet fica na
 *     tela, pronto para copiar, porque quem monta o receptor não vai procurar documentação.
 *
 *  3. **O registro de entregas.** "O cliente diz que não recebeu" é indepurável sem ele.
 *  ============================================================================ */
@Component({
  selector: 'app-integracoes',
  imports: [FormsModule, DatePipe, Paginacao],
  templateUrl: './integracoes.html',
  styleUrl: './integracoes.css'
})
export class Integracoes implements OnInit {
  private servico = inject(WebhooksServico);
  private toast = inject(ToastServico);

  readonly eventos: { campo: CampoEvento; nome: EventoWebhook; descricao: string }[] = [
    { campo: 'emLeadCriado', nome: 'lead.criado', descricao: 'Contato criado, por qualquer caminho' },
    { campo: 'emLeadMovido', nome: 'lead.movido', descricao: 'Contato mudou de etapa no funil' },
    { campo: 'emVendaFechada', nome: 'venda.fechada', descricao: 'Contato marcado como ganho' },
    { campo: 'emVendaPerdida', nome: 'venda.perdida', descricao: 'Contato marcado como perdido' },
    {
      campo: 'emMensagemRecebida', nome: 'mensagem.recebida',
      descricao: 'Mensagem de entrada — o de MAIOR volume; ligue só se for usar'
    }
  ];

  webhook = signal<WebhookDto | null>(null);
  entregas = signal<EntregaWebhookDto[]>([]);
  carregando = signal(true);
  erro = signal('');

  // ---- formulário
  fUrl = signal('');
  fAtivo = signal(true);
  fSomenteIds = signal(false);
  fEventos = signal<Record<CampoEvento, boolean>>({
    emLeadCriado: true, emLeadMovido: true, emVendaFechada: true,
    emVendaPerdida: true, emMensagemRecebida: false
  });
  salvando = signal(false);
  erroForm = signal('');

  /** O segredo revelado NESTA sessão da tela. Ele nunca volta da API depois — sair da tela é
   *  perdê-lo, e o painel diz isso. */
  segredo = signal<SegredoRevelado | null>(null);

  testando = signal(false);
  resultadoTeste = signal<ResultadoTeste | null>(null);

  /** Qual entrega está com o payload aberto. */
  abertaId = signal<number | null>(null);

  pagina = signal(1);
  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  totalPaginas = computed(() => totalDePaginas(this.entregas().length));
  visiveis = computed(() => fatiar(this.entregas(), this.pagina()));
  alturaMinima = computed(() => this.totalPaginas() > 1 ? alturaMinimaDaTabela() : 0);

  /** Quantas falharam de vez. É o número que o dono precisa ver sem procurar. */
  falhas = computed(() => this.entregas().filter(e => e.status === 'falhou').length);

  configurado = computed(() => this.webhook() !== null);

  irPara(p: number) {
    this.pagina.set(p);
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  ngOnInit() { this.carregar(); }

  carregar() {
    this.servico.obter().subscribe({
      next: r => {
        // `?? null` e `?? []` não são paranoia: `undefined` NÃO é `null`, e `configurado()` passaria
        // a ser verdadeiro para um payload sem a chave — a tela renderizaria o cartão do webhook
        // lendo `.ativo` de nada. É o tipo de diferença que o TypeScript não vê, porque ele confia
        // no tipo declarado da resposta e a resposta vem da rede.
        this.webhook.set(r.webhook ?? null);
        this.entregas.set(r.entregas ?? []);
        this.carregando.set(false);
        this.erro.set('');

        if (r.webhook) {
          this.fUrl.set(r.webhook.url);
          this.fAtivo.set(r.webhook.ativo);
          this.fSomenteIds.set(r.webhook.somenteIds);
          this.fEventos.set({
            emLeadCriado: r.webhook.emLeadCriado,
            emLeadMovido: r.webhook.emLeadMovido,
            emVendaFechada: r.webhook.emVendaFechada,
            emVendaPerdida: r.webhook.emVendaPerdida,
            emMensagemRecebida: r.webhook.emMensagemRecebida
          });
        }

        if (this.pagina() > this.totalPaginas()) this.pagina.set(this.totalPaginas());
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar as integrações.');
        this.carregando.set(false);
      }
    });
  }

  marcado(campo: CampoEvento): boolean { return this.fEventos()[campo]; }

  alternarEvento(campo: CampoEvento) {
    this.fEventos.update(e => ({ ...e, [campo]: !e[campo] }));
  }

  // ---------------------------------------------------------------- salvar
  salvar() {
    const url = this.fUrl().trim();
    if (url.length === 0) { this.erroForm.set('Informe a URL do webhook.'); return; }

    this.salvando.set(true);
    this.erroForm.set('');
    this.servico.salvar({
      url,
      ativo: this.fAtivo(),
      somenteIds: this.fSomenteIds(),
      ...this.fEventos()
    }).subscribe({
      next: r => {
        this.salvando.set(false);
        // O segredo só vem na CRIAÇÃO. Numa atualização ele é nulo, e não pode apagar o que já
        // estava revelado na tela — quem acabou de criar e depois salvou de novo perderia a chave.
        if (r.segredo) this.segredo.set(r.segredo);
        this.toast.sucesso('Integração salva.');
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.erroForm.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  /** Regerar é o botão do vazamento — por isso a confirmação diz o PREÇO em vez de perguntar
   *  "tem certeza?". Um "tem certeza" genérico é clicado sem ler. */
  regerarSegredo() {
    if (!confirm(
      'Gerar um segredo novo?\n\n' +
      'O atual PARA DE ASSINAR imediatamente. Até você trocar a chave no seu sistema, ele vai ' +
      'receber entregas cuja assinatura não confere — e deve recusá-las.\n\n' +
      'Faça isso se o segredo vazou.')) return;

    this.servico.regerarSegredo().subscribe({
      next: s => {
        this.segredo.set(s);
        this.toast.sucesso('Segredo novo gerado. Copie agora — ele não aparece de novo.');
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível gerar.')
    });
  }

  remover() {
    if (!confirm(
      'Remover a integração?\n\n' +
      'O Nexora para de avisar seu sistema na hora. O registro das entregas continua aqui até ' +
      'completar 30 dias.')) return;

    this.servico.remover().subscribe({
      next: () => {
        this.segredo.set(null);
        this.resultadoTeste.set(null);
        this.toast.sucesso('Integração removida.');
        this.carregar();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível remover.')
    });
  }

  // ---------------------------------------------------------------- teste
  testar() {
    this.testando.set(true);
    this.resultadoTeste.set(null);
    this.servico.testar().subscribe({
      next: r => {
        this.testando.set(false);
        this.resultadoTeste.set(r);
        if (r.ok) this.toast.sucesso('Seu servidor recebeu e aceitou o evento de teste.');
        this.carregar();
      },
      error: e => {
        this.testando.set(false);
        this.resultadoTeste.set({ ok: false, codigo: null, erro: e.error?.erro ?? 'Falhou.' });
        this.carregar();
      }
    });
  }

  // ---------------------------------------------------------------- entregas
  alternarPayload(id: number) {
    this.abertaId.update(a => (a === id ? null : id));
  }

  reenviar(e: EntregaWebhookDto) {
    this.servico.reenviar(e.id).subscribe({
      next: () => {
        this.toast.sucesso('Entrega devolvida para a fila. A próxima rodada vai tentar de novo.');
        this.carregar();
      },
      error: err => this.toast.erro(err.error?.erro ?? 'Não foi possível reenviar.')
    });
  }

  /** O payload indentado, para leitura. O que foi assinado é a versão compacta — indentar aqui é
   *  só apresentação, e o texto que o dono copia para conferir a assinatura NÃO é este. */
  formatado(payload: string): string {
    try { return JSON.stringify(JSON.parse(payload), null, 2); }
    catch { return payload; }
  }

  copiar(texto: string, oque: string) {
    navigator.clipboard.writeText(texto).then(
      () => this.toast.sucesso(`${oque} copiado.`),
      () => this.toast.erro('Não foi possível copiar. Selecione e copie à mão.')
    );
  }

  /** O código que valida a assinatura, do lado do receptor.
   *
   *  ===================== POR QUE ELE FICA NA TELA =====================
   *  Sem isto, ninguém valida — e uma assinatura que ninguém confere não protege nada. O snippet
   *  precisa de três coisas que são fáceis de errar sozinho: assinar `timestamp.corpo` (e não só
   *  o corpo), usar o corpo CRU (reserializar muda bytes e quebra o HMAC), e comparar em tempo
   *  constante.
   *  ==================================================================== */
  exemploNode(): string {
    return `// Node.js / Express — valide ANTES de processar
const crypto = require('crypto');

// express.raw: o corpo precisa chegar CRU. \`express.json()\` reserializa e o HMAC deixa de bater.
app.post('/nexora', express.raw({ type: 'application/json' }), (req, res) => {
  const assinatura = req.get('X-Nexora-Assinatura') || '';
  const timestamp  = req.get('X-Nexora-Timestamp') || '';
  const corpo      = req.body.toString('utf8');

  // 1. Recusa o replay: entrega com mais de 5 minutos não vale mais.
  if (Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return res.sendStatus(408);

  // 2. Confere a assinatura sobre \`timestamp.corpo\`.
  const esperada = 'sha256=' + crypto
    .createHmac('sha256', process.env.NEXORA_SEGREDO)
    .update(timestamp + '.' + corpo)
    .digest('hex');

  // 3. Comparação em tempo constante — \`===\` vaza a assinatura byte a byte pelo relógio.
  const ok = assinatura.length === esperada.length && crypto.timingSafeEqual(
    Buffer.from(assinatura), Buffer.from(esperada));
  if (!ok) return res.sendStatus(401);

  const evento = JSON.parse(corpo);

  // 4. Idempotência: as 3 tentativas trazem o MESMO \`evento.id\`.
  //    Se você já processou este id, responda 200 e não faça nada.

  res.sendStatus(200);   // responda rápido; processe depois
});`;
  }

  rotuloStatus(s: string): string {
    switch (s) {
      case 'entregue': return 'entregue';
      case 'pendente': return 'na fila';
      case 'falhou': return 'falhou';
      default: return s;
    }
  }
}

type CampoEvento =
  'emLeadCriado' | 'emLeadMovido' | 'emVendaFechada' | 'emVendaPerdida' | 'emMensagemRecebida';
