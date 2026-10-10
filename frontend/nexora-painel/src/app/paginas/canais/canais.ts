import {
  Component, ElementRef, OnDestroy, OnInit, ViewChild, computed, inject, output, signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  POR_PAGINA, Paginacao, alturaMinimaDaTabela, rolarParaTopoDaTabela
} from '../../nucleo/paginacao/paginacao';
import { CanaisServico } from '../../nucleo/servicos/canais.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { baixarBlob } from '../../nucleo/download';
import {
  Canais as CanaisDto, CanalDto, LIMITE_MENSAGEM_CANAL, OrigemLead
} from '../../nucleo/modelos';
import { erroAo } from '../../nucleo/erros';

/** CANAIS DE CAPTAÇÃO — o painel da aba "QR Code e links" em `/captacao`.
 *
 *  ===================== É PAINEL, NÃO PÁGINA (NAV-1) =====================
 *  Nasceu com rota própria (`/canais`) e cabeçalho de página. Virou uma aba de Captação e perdeu
 *  `.pagina`, `<h1>` e o subtítulo — quem desenha o cabeçalho agora é o container. Continua
 *  buscando a própria lista e funcionando sozinho; `mudou` avisa o container para recalcular o
 *  resumo depois de qualquer escrita.
 *  ========================================================================
 *
 *  ===================== O QUE ESTA TELA PRECISA DEIXAR CLARO =====================
 *  O rastreio é FRÁGIL de propósito, e a tela não pode esconder isso. O canal gera um link
 *  `wa.me` com um código curto no texto pré-preenchido; quem escaneia pode apagar o texto antes
 *  de mandar, e vai acontecer. Quando acontece, o lead entra como `whatsapp` e ninguém fica
 *  sabendo de onde veio — que é melhor que atribuir ao canal errado.
 *
 *  Por isso o contador é apresentado como PISO, não como total, e o texto do link fica visível:
 *  quem cria o canal precisa ver a frase que o cliente dele vai mandar, porque é ela que decide
 *  se o código sobrevive ao envio.
 *  ================================================================================ */
@Component({
  selector: 'app-canais',
  imports: [FormsModule, Paginacao],
  templateUrl: './canais.html',
  styleUrl: './canais.css'
})
export class Canais implements OnInit, OnDestroy {
  private servico = inject(CanaisServico);
  private toast = inject(ToastServico);

  /** Alguma escrita aconteceu. O container de Captação usa isto para recalcular o resumo — que
   *  soma os dois canais e ficaria velho sem o aviso. */
  mudou = output<void>();

  readonly origens: { valor: OrigemLead; rotulo: string }[] = [
    { valor: 'qrcode', rotulo: 'QR Code (balcão, panfleto, vitrine)' },
    { valor: 'instagram', rotulo: 'Instagram (link na bio, story)' },
    { valor: 'facebook', rotulo: 'Facebook' },
    { valor: 'google', rotulo: 'Google (anúncio, perfil da empresa)' },
    { valor: 'site', rotulo: 'Site' },
    { valor: 'indicacao', rotulo: 'Indicação (parceiro)' },
    { valor: 'outro', rotulo: 'Outro' }
  ];

  lista = signal<CanalDto[]>([]);
  conexoes = signal<CanaisDto['conexoes']>([]);
  podeCriar = signal(false);
  leadsAtribuidos = signal(0);

  carregando = signal(true);
  erro = signal('');

  // ---- criação
  fNome = signal('');
  fConexaoId = signal<number | null>(null);
  fOrigem = signal<OrigemLead>('qrcode');
  /** A frase do link. Vazia = a Nexora usa a padrao ("Olá! Tenho interesse."). O codigo entra
   *  sozinho no fim, sempre — ver `CodigoCanal.TextoDoLink`. */
  fMensagem = signal('');

  /** O teto, para o `maxlength` e o contador. Vem do modelo compartilhado — o servidor tem o
   *  mesmo numero e e ele quem recusa. */
  readonly limite = LIMITE_MENSAGEM_CANAL;
  criando = signal(false);
  erroNovo = signal('');

  // ---- edição em linha
  editandoId = signal<number | null>(null);
  eNome = signal('');
  eConexaoId = signal<number | null>(null);
  eOrigem = signal<OrigemLead>('qrcode');
  eMensagem = signal('');

  /** Qual canal está com o QR aberto. Um por vez: dois QR na tela ao mesmo tempo é convite para
   *  imprimir o errado. */
  abertoId = signal<number | null>(null);
  /** O canal do QR aberto, guardado aqui e não procurado na lista: a lista é UMA página (AUD-XX,
   *  #21), e o canal recém-criado pode estar em outra. A recarga troca pela versão nova quando ele
   *  está na página aberta. */
  aberto = signal<CanalDto | null>(null);

  /** O `blob:` do SVG que está sendo exibido. Object URL e não `<img src="/api/...">` porque a
   *  rota exige `Authorization: Bearer`, e `<img>` navega sem cabeçalho. */
  qrUrl = signal<string | null>(null);
  carregandoQr = signal(false);

  removendo = signal<CanalDto | null>(null);

  /** Quantos canais estão com o número desconectado: o link deles está quebrado AGORA, e o
   *  material já impresso aponta para um número que não atende. Contado no servidor (AUD-XX). */
  semNumero = signal(0);

  /** ===================== A PÁGINA VEM DO SERVIDOR (AUD-XX, #21) =====================
   *  `GET /api/canais` devolve uma página com o total e as páginas prontos — e os números do
   *  topo (leads, sem número, pode criar) de TODOS os canais. A tela recortava a lista inteira e
   *  dividia o tamanho dela por 20; agora só desenha o que veio.
   *  ================================================================================ */
  pagina = signal(1);
  totalPaginas = signal(1);
  totalCanais = signal(0);

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  alturaMinima = computed(() => this.totalPaginas() > 1 ? alturaMinimaDaTabela() : 0);

  irPara(p: number) {
    this.pagina.set(p);
    this.carregar();
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  ngOnInit() { this.carregar(); }

  ngOnDestroy() { this.soltarQr(); }

  // ---------------------------------------------------------------- lista
  carregar() {
    this.servico.listar(this.pagina(), POR_PAGINA).subscribe({
      next: r => {
        // A página esvaziou (saiu a última linha dela): vai direto para a última que existe,
        // pelo `totalPaginas` do servidor (AUD-XX, #21).
        if (r.itens.length === 0 && r.totalCount > 0 && this.pagina() > r.totalPaginas) {
          this.pagina.set(r.totalPaginas);
          this.carregar();
          return;
        }

        this.lista.set(r.itens);
        this.totalCanais.set(r.totalCount);
        this.totalPaginas.set(r.totalPaginas);
        this.semNumero.set(r.semNumero);
        this.conexoes.set(r.conexoes);
        this.podeCriar.set(r.podeCriar);
        this.leadsAtribuidos.set(r.leadsAtribuidos);
        this.carregando.set(false);
        this.erro.set('');

        if (this.fConexaoId() === null && r.conexoes.length > 0) {
          this.fConexaoId.set(r.conexoes[0].id);
        }

        // O QR aberto mostra a versão nova do canal, quando ele está nesta página (o nome ou a
        // frase podem ter mudado). Fora dela, fica como estava: a lista é uma página só.
        const abertoNaPagina = r.itens.find(c => c.id === this.abertoId());
        if (abertoNaPagina !== undefined) this.aberto.set(abertoNaPagina);

      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar os canais.');
        this.carregando.set(false);
      }
    });
  }

  /** Recarrega E avisa o container. Um método só para as escritas: se cada uma chamasse
   *  `carregar()` na mão, bastaria uma esquecer o `mudou` para o resumo de Captação ficar velho
   *  — e resumo velho não parece defeito, parece número. */
  private aposEscrita() {
    this.carregar();
    this.mudou.emit();
  }

  // ---------------------------------------------------------------- QR
  abrir(c: CanalDto) {
    if (this.abertoId() === c.id) { this.fechar(); return; }

    this.soltarQr();
    this.abertoId.set(c.id);
    this.aberto.set(c);

    if (c.link === null) return;   // sem número pareado não há QR; a tela explica

    this.carregandoQr.set(true);
    this.servico.svg(c.id).subscribe({
      next: b => {
        this.qrUrl.set(URL.createObjectURL(b));
        this.carregandoQr.set(false);
      },
      error: e => {
        this.carregandoQr.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível gerar o QR Code.');
      }
    });
  }

  fechar() {
    this.soltarQr();
    this.abertoId.set(null);
    this.aberto.set(null);
  }

  /** Devolve o blob ao navegador. Sem isto, cada abertura deixa uma imagem presa na memória da
   *  aba até o recarregamento. */
  private soltarQr() {
    const url = this.qrUrl();
    if (url) URL.revokeObjectURL(url);
    this.qrUrl.set(null);
  }

  baixarSvg(c: CanalDto) {
    this.servico.svg(c.id).subscribe({
      next: b => baixarBlob(`${c.nomeArquivo}.svg`, b),
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível baixar o SVG.')
    });
  }

  baixarPng(c: CanalDto) {
    this.servico.png(c.id).subscribe({
      next: b => baixarBlob(`${c.nomeArquivo}.png`, b),
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível baixar o PNG.')
    });
  }

  // ---------------------------------------------------------------- criar
  criar() {
    const nome = this.fNome().trim();
    const conexaoId = this.fConexaoId();

    if (nome.length < 2) { this.erroNovo.set('Dê um nome ao canal.'); return; }
    if (conexaoId === null) { this.erroNovo.set('Escolha o número que vai atender.'); return; }

    this.criando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nome, conexaoId, this.fOrigem(), this.fMensagem().trim() || null).subscribe({
      next: r => {
        this.criando.set(false);
        this.fNome.set('');
        this.toast.sucesso(`"${nome}" criado. Baixe o QR Code e o link.`);
        this.mudou.emit();

        // O canal criado vem na resposta e o QR abre direto — quem acabou de criar veio buscar
        // a imagem. Ele pode cair em outra página da lista (AUD-XX, #21), por isso não é
        // procurado nela.
        this.carregar();
        this.abrir(r.canal);
      },
      error: e => {
        this.criando.set(false);
        this.erroNovo.set(erroAo(e, 'criar o canal'));
      }
    });
  }

  // ---------------------------------------------------------------- editar
  editar(c: CanalDto) {
    this.editandoId.set(c.id);
    this.eNome.set(c.nome);
    this.eConexaoId.set(c.conexaoId);
    this.eOrigem.set(c.origem);
    // A FRASE, sem o codigo — `c.texto` traz o resultado final e recolocá-lo aqui duplicaria o
    // codigo a cada edicao.
    this.eMensagem.set(c.mensagem ?? '');
  }

  cancelarEdicao() { this.editandoId.set(null); }

  salvarEdicao(c: CanalDto) {
    const conexaoId = this.eConexaoId();
    if (conexaoId === null) { this.toast.erro('Escolha o número que vai atender.'); return; }

    // Trocar o número TROCA O LINK, e o QR já impresso continua apontando para o antigo. Não é
    // proibido — pode ser exatamente o que a empresa quer, se aposentou o número velho —, mas
    // quem faz precisa saber o preço antes, não depois.
    if (conexaoId !== c.conexaoId && !confirm(
      `Mudar "${c.nome}" para outro número?\n\n` +
      `O link muda AGORA. Todo QR Code já impresso continua apontando para o número antigo, e ` +
      `não há como corrigir o que já foi distribuído.`)) return;

    this.servico.atualizar(c.id, this.eNome().trim(), conexaoId, this.eOrigem(),
                          this.eMensagem().trim() || null).subscribe({
      next: () => {
        this.editandoId.set(null);
        this.toast.sucesso('Canal atualizado.');
        this.fechar();
        this.aposEscrita();
      },
      error: e => this.toast.erro(erroAo(e, 'salvar o canal'))
    });
  }

  alternarAtivo(c: CanalDto) {
    if (c.ativo && !confirm(
      `Desativar "${c.nome}"?\n\n` +
      `O link e o QR continuam funcionando — quem escanear ainda cai na sua conversa. O que para ` +
      `é a ATRIBUIÇÃO: os leads passam a entrar como "WhatsApp", sem dizer que vieram daqui.`)) return;

    this.servico.alternarAtivo(c.id, !c.ativo).subscribe({
      next: () => {
        this.toast.sucesso(c.ativo
          ? `"${c.nome}" desativado. Os leads continuam entrando, sem atribuição.`
          : `"${c.nome}" ativado.`);
        this.aposEscrita();
      },
      error: e => this.toast.erro(erroAo(e, 'ativar ou desativar o canal'))
    });
  }

  // ---------------------------------------------------------------- remover
  pedirRemocao(c: CanalDto) { this.removendo.set(c); }
  cancelarRemocao() { this.removendo.set(null); }

  confirmarRemocao() {
    const alvo = this.removendo();
    if (alvo === null) return;

    this.servico.remover(alvo.id).subscribe({
      next: () => {
        this.removendo.set(null);
        if (this.abertoId() === alvo.id) this.fechar();
        this.toast.sucesso(`"${alvo.nome}" apagado.`);
        this.aposEscrita();
      },
      error: e => this.toast.erro(erroAo(e, 'apagar o canal'))
    });
  }

  // ---------------------------------------------------------------- apoio
  copiar(texto: string | null, oque: string) {
    if (texto === null) return;
    navigator.clipboard.writeText(texto).then(
      () => this.toast.sucesso(`${oque} copiado.`),
      () => this.toast.erro('Não foi possível copiar. Selecione e copie à mão.')
    );
  }

  rotuloOrigem(origem: OrigemLead): string {
    return this.origens.find(o => o.valor === origem)?.rotulo ?? origem;
  }
}
